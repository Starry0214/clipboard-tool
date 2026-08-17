using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ClipboardTool;

/// <summary>SQLite 持久化：写入、查询、裁剪、置顶保护。所有方法须在 UI 线程调用。</summary>
public sealed class ClipboardStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _imagesDir;
    private readonly string _dataDir;

    public ClipboardStore(string dataDir)
    {
        _dataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(dataDir);
        _imagesDir = Path.Combine(dataDir, "images");
        Directory.CreateDirectory(_imagesDir);
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
        // 旧库无 source 列时补列（多端同步）
        var hasSource = false;
        using (var probe2 = _conn.CreateCommand())
        {
            probe2.CommandText = "PRAGMA table_info(entries)";
            using var r2 = probe2.ExecuteReader();
            while (r2.Read())
                if (r2.GetString(1) == "source")
                    hasSource = true;
        }
        if (!hasSource)
        {
            using var alter2 = _conn.CreateCommand();
            alter2.CommandText = "ALTER TABLE entries ADD COLUMN source TEXT NOT NULL DEFAULT 'local'";
            alter2.ExecuteNonQuery();
        }
    }

    public int MaxEntries { get; set; } = 500;

    /// <summary>原图保存为 PNG 文件（图片不再以 BLOB 入库），返回文件路径。
    /// 文件名用时间戳（可读，列表显示名称时避免 UUID 哈希值）；同一秒多张图追加 (1)/(2) 后缀。</summary>
    public string SaveImageFile(byte[] png)
    {
        var stem = $"剪贴板_{DateTimeOffset.Now.ToUnixTimeSeconds()}";
        var path = Path.Combine(_imagesDir, $"{stem}.png");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(_imagesDir, $"{stem} ({i}).png");
        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>按原始文件名保存图片到 images 目录（资源管理器复制图片/同步接收时保留原名），重名追加 (1)/(2) 后缀。</summary>
    public string SaveImageFileAs(string name, byte[] png)
    {
        var safe = Path.GetFileName(name);
        var path = Path.Combine(_imagesDir, safe);
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, png);
            return path;
        }
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(_imagesDir, $"{stem} ({i}){ext}");
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, png);
                return path;
            }
        }
    }

    /// <summary>新增条目。按内容哈希去重（文本/文件按内容、图片按像素字节），重复则刷新时间并移顶。返回是否新增（去重时返回 false）。</summary>
    public bool Add(Entry e)
    {
        var hash = ComputeHash(e);
        if (hash is null)
            return false; // 文件不可读：不建坏条目（不降级路径哈希）
        if (ExistsByHash(hash))
        {
            Touch(hash);
            return false;
        }

        using var cmd = _conn.CreateCommand();
        // 图片原图以文件存储（Content=文件路径），image 列不再写入
        cmd.CommandText = """
            INSERT INTO entries (type, content, hash, thumb, pinned, created_at, source)
            VALUES ($type, $content, $hash, $thumb, $pinned, $created, $source)
            """;
        cmd.Parameters.AddWithValue("$type", e.Type);
        cmd.Parameters.AddWithValue("$content", e.Content);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$thumb", (object?)e.Thumb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pinned", e.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", e.CreatedAt);
        cmd.Parameters.AddWithValue("$source", string.IsNullOrEmpty(e.Source) ? "local" : e.Source);
        cmd.ExecuteNonQuery();

        Trim();
        return true;
    }

    /// <summary>内容哈希（跨端去重/删除/置顶的身份依据）。文本=SHA256(type\0content)；
    /// 图片/文件=SHA256(文件字节)。文件不可读时返回 null，调用方跳过入库——
    /// 绝不降级为路径哈希（路径哈希会让去重/删除/同步全部失配，本会话曾因此踩坑）。</summary>
    private static string? ComputeHash(Entry e)
    {
        if (e.Type is "file" or "image")
        {
            // 图片优先用内存字节（捕获/同步时已持有），否则读文件字节（图片原图已是文件存储）
            if (e.Type == "image" && e.Image is not null)
                return Convert.ToHexString(SHA256.HashData(e.Image));
            if (string.IsNullOrEmpty(e.Content))
                return null;
            try
            {
                return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(e.Content)));
            }
            catch (Exception)
            {
                return null; // 文件不可读（被占用/已删除）：跳过，不建坏条目
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.Type + "\u0000" + e.Content)));
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

    /// <summary>裁剪超出上限的最旧非置顶条目，并清理被删条目的图片/同步文件。降低上限后由调用方显式触发。</summary>
    public void Trim()
    {
        var files = new List<string>();
        using (var sel = _conn.CreateCommand())
        {
            sel.CommandText = """
                SELECT content FROM entries
                WHERE pinned = 0 AND type IN ('image', 'file') AND id NOT IN (
                    SELECT id FROM entries ORDER BY pinned DESC, created_at DESC LIMIT $max
                )
                """;
            sel.Parameters.AddWithValue("$max", Math.Max(MaxEntries, 1));
            using var reader = sel.ExecuteReader();
            while (reader.Read())
                files.Add(reader.GetString(0));
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM entries WHERE pinned = 0 AND id NOT IN (
                SELECT id FROM entries ORDER BY pinned DESC, created_at DESC LIMIT $max
            )
            """;
        cmd.Parameters.AddWithValue("$max", Math.Max(MaxEntries, 1));
        cmd.ExecuteNonQuery();

        foreach (var f in files)
            TryDeleteFile(f);
    }

    /// <summary>查询历史：置顶优先、时间倒序。列表查询不含原图 BLOB（仅缩略图）。type/source 为空表示全部。</summary>
    public List<Entry> Query(string? search = null, string? type = null, string? source = null)
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
        if (!string.IsNullOrEmpty(source))
        {
            where.Add("source = $source");
            cmd.Parameters.AddWithValue("$source", source);
        }
        cmd.CommandText = where.Count == 0
            ? "SELECT id, type, content, hash, thumb, pinned, created_at, source FROM entries ORDER BY pinned DESC, created_at DESC"
            : $"SELECT id, type, content, hash, thumb, pinned, created_at, source FROM entries WHERE {string.Join(" AND ", where)} ORDER BY pinned DESC, created_at DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var content = reader.GetString(2);
            var entryType = reader.GetString(1);
            list.Add(new Entry
            {
                Id = reader.GetInt64(0),
                Type = entryType,
                Content = content,
                Hash = reader.IsDBNull(3) ? "" : reader.GetString(3),
                // 列表显示截断：超长文本（如整篇文档）会让 WPF 换行测量全文，布局卡死；
                // 图片/文件条目只显示文件名（完整路径在 Content，预览/打开时用）
                DisplayContent = entryType switch
                {
                    "image" or "file" => Path.GetFileName(content),
                    _ => content.Length > 200 ? content[..200] : content,
                },
                Thumb = reader.IsDBNull(4) ? null : (byte[])reader[4],
                Pinned = reader.GetInt64(5) != 0,
                CreatedAt = reader.GetInt64(6),
                Source = reader.IsDBNull(7) ? "local" : reader.GetString(7),
            });
        }
        return list;
    }

    /// <summary>查询缩略图缺失的图片条目（启动自愈用）：返回 (id, content 文件路径)。</summary>
    public List<(long Id, string Content)> GetThumblessImages()
    {
        var list = new List<(long, string)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, content FROM entries WHERE type = 'image' AND thumb IS NULL";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetInt64(0), reader.GetString(1)));
        return list;
    }

    /// <summary>查询全部图片条目：返回 (id, content 文件路径, thumb)。启动自愈用——thumb 存在但全透明的旧数据也要重生成。</summary>
    public List<(long Id, string Content, byte[]? Thumb)> GetAllImages()
    {
        var list = new List<(long, string, byte[]?)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, content, thumb FROM entries WHERE type = 'image'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : (byte[])reader[2]));
        return list;
    }

    /// <summary>更新条目缩略图（启动自愈补生成）。</summary>
    public void UpdateThumb(long id, byte[] thumb)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE entries SET thumb = $thumb WHERE id = $id";
        cmd.Parameters.AddWithValue("$thumb", thumb);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>取含原图的完整条目（回贴用）。图片原图优先从 DB BLOB（旧数据）读取，否则从文件读取。</summary>
    public Entry? GetById(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, type, content, hash, thumb, image, pinned, created_at, source FROM entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        var entry = new Entry
        {
            Id = reader.GetInt64(0),
            Type = reader.GetString(1),
            Content = reader.GetString(2),
            Hash = reader.IsDBNull(3) ? "" : reader.GetString(3),
            Thumb = reader.IsDBNull(4) ? null : (byte[])reader[4],
            Image = reader.IsDBNull(5) ? null : (byte[])reader[5],
            Pinned = reader.GetInt64(6) != 0,
            CreatedAt = reader.GetInt64(7),
            Source = reader.IsDBNull(8) ? "local" : reader.GetString(8),
        };
        if (entry.Type == "image" && entry.Image is null && !string.IsNullOrEmpty(entry.Content))
        {
            try
            {
                if (File.Exists(entry.Content))
                    entry.Image = File.ReadAllBytes(entry.Content);
            }
            catch (IOException)
            {
            }
        }
        return entry;
    }

    public void SetPinned(long id, bool pinned)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE entries SET pinned = $pinned WHERE id = $id";
        cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>按内容哈希设置置顶（同步 pin 消息应用）。</summary>
    public void SetPinnedByHash(string hash, bool pinned)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE entries SET pinned = $pinned WHERE hash = $hash COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.ExecuteNonQuery();
    }

    /// <summary>刷新条目时间戳使其移到非置顶区顶部（预览后使用，不改变置顶状态）。</summary>
    public void TouchById(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE entries SET created_at = $created WHERE id = $id";
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        string? file = null;
        using (var sel = _conn.CreateCommand())
        {
            sel.CommandText = "SELECT content FROM entries WHERE id = $id AND type IN ('image', 'file')";
            sel.Parameters.AddWithValue("$id", id);
            var v = sel.ExecuteScalar();
            if (v is string s)
                file = s;
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();

        TryDeleteFile(file);
    }

    /// <summary>按内容哈希删除条目（跨端同步删除用），并清理图片/同步文件。
    /// 用 NOCASE 匹配：本库 hash 为大写 hex（Convert.ToHexString），服务器/手机端为小写。</summary>
    public void DeleteByHash(string hash)
    {
        var files = new List<string>();
        using (var sel = _conn.CreateCommand())
        {
            sel.CommandText = "SELECT content FROM entries WHERE hash = $hash COLLATE NOCASE AND type IN ('image', 'file')";
            sel.Parameters.AddWithValue("$hash", hash);
            using var reader = sel.ExecuteReader();
            while (reader.Read())
                files.Add(reader.GetString(0));
        }
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE hash = $hash COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.ExecuteNonQuery();
        foreach (var f in files)
            TryDeleteFile(f);
    }

    public void Clear()
    {
        // 清空历史但保留置顶条目（与 Trim 的置顶保护一致）
        var files = new List<string>();
        using (var sel = _conn.CreateCommand())
        {
            sel.CommandText = "SELECT content FROM entries WHERE pinned = 0 AND type IN ('image', 'file')";
            using var reader = sel.ExecuteReader();
            while (reader.Read())
                files.Add(reader.GetString(0));
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE pinned = 0";
        cmd.ExecuteNonQuery();

        foreach (var f in files)
            TryDeleteFile(f);
    }

    private void TryDeleteFile(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            var full = Path.GetFullPath(path);
            // 只删数据目录内的副本（图片/同步文件）；本机复制文件的源路径只删记录、绝不触碰
            if (!full.StartsWith(_dataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;
            File.Delete(full);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>清理 images/files 目录中数据库已无对应条目的孤儿文件
    /// （旧版本删除同步文件条目时不清理文件、以及历史版本遗留的孤儿）。启动时调用一次。</summary>
    public void CleanupOrphanFiles()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT content FROM entries WHERE type IN ('image', 'file')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (reader.GetString(0) is { Length: > 0 } path)
                    referenced.Add(path);
        }
        foreach (var dir in new[] { _imagesDir, Path.Combine(Path.GetDirectoryName(_imagesDir)!, "files") })
        {
            if (!Directory.Exists(dir))
                continue;
            foreach (var f in Directory.EnumerateFiles(dir))
                if (!referenced.Contains(f))
                {
                    try { File.Delete(f); }
                    catch (IOException) { }
                }
        }
    }

    public void Dispose() => _conn.Dispose();
}
