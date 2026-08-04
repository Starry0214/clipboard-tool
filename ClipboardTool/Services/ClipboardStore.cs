using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ClipboardTool;

/// <summary>SQLite 持久化：写入、查询、裁剪、置顶保护。所有方法须在 UI 线程调用。</summary>
public sealed class ClipboardStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public ClipboardStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _conn = new SqliteConnection($"Data Source={Path.Combine(dataDir, "clipboard.db")}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                type TEXT NOT NULL,
                content TEXT NOT NULL DEFAULT '',
                hash TEXT NOT NULL DEFAULT '',
                thumb BLOB NULL,
                image BLOB NULL,
                pinned INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_entries_order ON entries (pinned DESC, created_at DESC);
            """;
        cmd.ExecuteNonQuery();
        // 旧库无 hash 列时补列
        var hasHash = false;
        using (var probe = _conn.CreateCommand())
        {
            probe.CommandText = "PRAGMA table_info(entries)";
            using var r = probe.ExecuteReader();
            while (r.Read())
                if (r.GetString(1) == "hash")
                    hasHash = true;
        }
        if (!hasHash)
        {
            using var alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE entries ADD COLUMN hash TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();
        }
    }

    public int MaxEntries { get; set; } = 500;

    /// <summary>新增条目。按内容哈希去重（文本/文件按内容、图片按像素字节），重复则刷新时间并移顶。</summary>
    public void Add(Entry e)
    {
        var hash = ComputeHash(e);
        if (ExistsByHash(hash))
        {
            Touch(hash);
            return;
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entries (type, content, hash, thumb, image, pinned, created_at)
            VALUES ($type, $content, $hash, $thumb, $image, $pinned, $created)
            """;
        cmd.Parameters.AddWithValue("$type", e.Type);
        cmd.Parameters.AddWithValue("$content", e.Content);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$thumb", (object?)e.Thumb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$image", (object?)e.Image ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pinned", e.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", e.CreatedAt);
        cmd.ExecuteNonQuery();

        Trim();
    }

    private static string ComputeHash(Entry e)
    {
        var sha = SHA256.HashData(e.Type == "image" && e.Image is not null
            ? e.Image
            : Encoding.UTF8.GetBytes(e.Type + "\u0000" + e.Content));
        return Convert.ToHexString(sha);
    }

    private bool ExistsByHash(string hash)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM entries WHERE hash = $hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private void Touch(string hash)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE entries SET created_at = $created WHERE hash = $hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>裁剪超出上限的最旧非置顶条目。降低上限后由调用方显式触发。</summary>
    public void Trim()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM entries WHERE pinned = 0 AND id NOT IN (
                SELECT id FROM entries ORDER BY pinned DESC, created_at DESC LIMIT $max
            )
            """;
        cmd.Parameters.AddWithValue("$max", Math.Max(MaxEntries, 1));
        cmd.ExecuteNonQuery();
    }

    /// <summary>查询历史：置顶优先、时间倒序。列表查询不含原图 BLOB（仅缩略图）。type 为空表示全部类型。</summary>
    public List<Entry> Query(string? search = null, string? type = null)
    {
        var list = new List<Entry>();
        using var cmd = _conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            // 图片条目不参与关键词搜索（规格 S2）；转义 LIKE 通配符
            var kw = search!.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            where.Add("type != 'image' AND content LIKE $kw ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$kw", $"%{kw}%");
        }
        if (!string.IsNullOrEmpty(type))
        {
            where.Add("type = $type");
            cmd.Parameters.AddWithValue("$type", type);
        }
        cmd.CommandText = where.Count == 0
            ? "SELECT id, type, content, thumb, pinned, created_at FROM entries ORDER BY pinned DESC, created_at DESC"
            : $"SELECT id, type, content, thumb, pinned, created_at FROM entries WHERE {string.Join(" AND ", where)} ORDER BY pinned DESC, created_at DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Entry
            {
                Id = reader.GetInt64(0),
                Type = reader.GetString(1),
                Content = reader.GetString(2),
                Thumb = reader.IsDBNull(3) ? null : (byte[])reader[3],
                Pinned = reader.GetInt64(4) != 0,
                CreatedAt = reader.GetInt64(5),
            });
        }
        return list;
    }

    /// <summary>取含原图的完整条目（回贴用）。</summary>
    public Entry? GetById(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, type, content, thumb, image, pinned, created_at FROM entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new Entry
        {
            Id = reader.GetInt64(0),
            Type = reader.GetString(1),
            Content = reader.GetString(2),
            Thumb = reader.IsDBNull(3) ? null : (byte[])reader[3],
            Image = reader.IsDBNull(4) ? null : (byte[])reader[4],
            Pinned = reader.GetInt64(5) != 0,
            CreatedAt = reader.GetInt64(6),
        };
    }

    public void SetPinned(long id, bool pinned)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE entries SET pinned = $pinned WHERE id = $id";
        cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Clear()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM entries";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
