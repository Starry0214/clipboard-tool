namespace ClipboardTool;

/// <summary>剪贴板历史条目。</summary>
public sealed record Entry
{
    public long Id { get; set; }
    public string Type { get; set; } = "text"; // text | image | file
    public string Content { get; set; } = "";
    /// <summary>列表显示用文本（text 条目截断，防超长文本拖垮 WPF 布局）；完整内容始终在 Content。</summary>
    public string DisplayContent { get; set; } = "";
    public byte[]? Thumb { get; set; }
    public byte[]? Image { get; set; }
    public bool Pinned { get; set; }
    public long CreatedAt { get; set; }
    public string Source { get; set; } = "local"; // local | phone（同步来源）
}
