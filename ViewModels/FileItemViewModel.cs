using System.IO;
using NameTool.Infrastructure;
using NameTool.Services;

namespace NameTool.ViewModels;

public enum DisplayMode
{
    List,
    Editor
}

public sealed class FileItemViewModel : ObservableObject
{
    private string _filePath = string.Empty;
    private string _preview = string.Empty;
    private string _content = string.Empty;
    private bool? _isSuccess;
    private bool _isProcessing;
    private bool _isChecked = true;
    private bool _previewInScope = true;
    private DisplayMode _mode = DisplayMode.List;

    /// <summary>拖入时磁盘上的**原始正文**（未经任何规则处理）。语言过滤的基准。</summary>
    private string? _originalContent;

    /// <summary>程序最后一次写进 <see cref="Content"/> 的正文（用来区分「程序算出来的」和「用户手打的」）。</summary>
    private string? _programmaticContent;

    public required string FilePath
    {
        get => _filePath;
        set
        {
            if (SetProperty(ref _filePath, value))
            {
                RaisePropertyChanged(nameof(OriginalFileName));
                RaisePropertyChanged(nameof(IsTextFile));
            }
        }
    }

    public string OriginalFileName => Path.GetFileName(FilePath);

    /// <summary>列表里显示的图标资源键（本地列表只有文件，按扩展名取）。</summary>
    public string IconKey => FileIcon.ForFile(FilePath);

    public string Preview
    {
        get => _preview;
        set => SetProperty(ref _preview, value);
    }

    public bool? IsSuccess
    {
        get => _isSuccess;
        set => SetProperty(ref _isSuccess, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    /// <summary>
    /// 列表最前面那个复选框：**是否参与本次处理**（默认勾选）。
    /// 只有勾选的文件才会被改名 / 改内容，没勾选的原样不动。
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    /// <summary>
    /// 「预览」里显示的名称是否**真的会生效**。
    /// 未勾选的行**不显示预览**（格子里是空的），悬停提示写明「未勾选，本次不会处理」——
    /// 早先给它们显示一个参考值，结果调一下批量序号这些行的预览也跟着变，容易误以为它们也会被改。
    /// </summary>
    public bool PreviewInScope
    {
        get => _previewInScope;
        set
        {
            if (SetProperty(ref _previewInScope, value))
            {
                RaisePropertyChanged(nameof(PreviewTip));
            }
        }
    }

    /// <summary>「预览」列的悬停提示：不在处理范围时说明原因，范围内不弹提示（预览本身就显示在格子里）。</summary>
    public string? PreviewTip => PreviewInScope ? null : "未勾选，本次不会处理";

    public string Content
    {
        get => _content;
        set => SetProperty(ref _content, value);
    }

    /// <summary>拖入时磁盘上的原始正文（未经规则处理）。空表示没读过正文（非文本类文件）。</summary>
    public string? OriginalContent => _originalContent;

    /// <summary>程序最后一次写入 <see cref="Content"/> 的内容。</summary>
    public string? ProgrammaticContent => _programmaticContent;

    /// <summary>
    /// 用户是否**手工改过**正文：编辑器里的内容与程序最后一次写入的不一致就认为改过。
    /// 处理时据此决定「以编辑器内容为准」还是「回到原始正文重新按当前规则推导」。
    /// </summary>
    public bool IsContentEdited => _originalContent is not null
        && !string.Equals(_content, _programmaticContent, StringComparison.Ordinal);

    /// <summary>记下磁盘上的原始正文（拖入 / 重新从磁盘读取时调用）。</summary>
    public void SetOriginalContent(string content) => _originalContent = content;

    /// <summary>由程序（加载、语言勾选刷新）写入正文：同时记下这次写入的内容。</summary>
    public void SetProgrammaticContent(string content)
    {
        _programmaticContent = content;
        Content = content;
    }

    /// <summary>
    /// 保存到磁盘之后调用：磁盘上的内容已经等于编辑器内容，
    /// 基准快照与「程序写入」快照一起对齐，编辑标记随之清零。
    /// </summary>
    public void MarkSaved()
    {
        _originalContent = _content;
        _programmaticContent = _content;
    }

    public bool IsTextFile => TextBatchProcessor.TextExts.Contains(Path.GetExtension(FilePath).ToLowerInvariant());

    public DisplayMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }
}
