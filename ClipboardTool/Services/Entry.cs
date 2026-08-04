namespace ClipboardTool;

/// <summary>剪贴板历史条目。</summary>
public sealed record Entry
{
    public long Id { get; set; }
    public string Type { get; set; } = "text"; // text | image | file
    public string Content { get; set; } = "";
    public byte[]? Thumb { get; set; }
    public byte[]? Image { get; set; }
    public bool Pinned { get; set; }
    public long CreatedAt { get; set; }
}
