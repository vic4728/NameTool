using System.Globalization;
using System.IO;
using NameTool.Infrastructure;
using NameTool.Services.N115;

namespace NameTool.ViewModels;

/// <summary>115 网盘目录里的一行（文件或文件夹）。</summary>
public sealed class N115ItemViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _preview = string.Empty;
    private bool _previewChanged;
    private bool _previewError;
    private bool _inRenameScope = true;

    public required string Id { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>
    /// 列表首行的「返回上级」伪条目。它不是网盘里真实的条目：
    /// 不参与多选统计、不参与改名、也不能被「只选文件 / 只选文件夹」选中。
    /// </summary>
    public bool IsParentEntry { get; init; }

    public long Size { get; init; }

    /// <summary>
    /// 修改时间（Unix 秒，本地时区换算前的原始值）。0 表示服务端没给。
    /// 列表按「修改时间」排序时用的就是这个数值，不能拿格式化后的文本去比字符串。
    /// </summary>
    public long UpdateTimeSeconds { get; init; }

    /// <summary>
    /// 列表里显示的修改时间。115 接口给的是 Unix 秒（10 位数字），
    /// 以前直接把这个原始值显示出来，所以看着「不对」；这里换算成本地时间再格式化。
    /// </summary>
    public string UpdateTimeText => UpdateTimeSeconds <= 0
        ? string.Empty
        : DateTimeOffset.FromUnixTimeSeconds(UpdateTimeSeconds)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 修改日期（yyyy-MM-dd），给序号模板变量 <c>{date}</c> 用；服务端没给时间时为 null（变量渲染为空）。
    /// </summary>
    public string? GetDateText() => UpdateTimeSeconds <= 0
        ? null
        : DateTimeOffset.FromUnixTimeSeconds(UpdateTimeSeconds)
            .ToLocalTime()
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                RaisePropertyChanged(nameof(PreviewTip));
            }
        }
    }

    public string TypeText
    {
        get
        {
            if (IsParentEntry) return string.Empty;
            if (IsDirectory) return "文件夹";
            var ext = Path.GetExtension(Name);
            return string.IsNullOrEmpty(ext) ? "文件" : ext.TrimStart('.').ToUpperInvariant();
        }
    }

    public string SizeText => IsDirectory || IsParentEntry ? string.Empty : FormatSize(Size);

    /// <summary>
    /// 「预览」列的内容：按右侧功能栏当前规则算出来的新名称。
    /// 空字符串表示这一行没有预览（首行「返回上级」是导航伪条目，不参与改名）。
    /// </summary>
    public string Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                RaisePropertyChanged(nameof(PreviewTip));
            }
        }
    }

    /// <summary>预览结果与当前名称不同 ⇒ 按当前规则这一项真的会被改名，界面用它加亮显示。</summary>
    public bool PreviewChanged
    {
        get => _previewChanged;
        private set => SetProperty(ref _previewChanged, value);
    }

    /// <summary>预览位置显示的不是新名称而是「规则参数不合法」的原因，界面用警示色显示。</summary>
    public bool PreviewError
    {
        get => _previewError;
        private set => SetProperty(ref _previewError, value);
    }

    /// <summary>
    /// 这一行是否**真的会被改名**：列表里有选中时只有选中的行才算，
    /// 没选中任何条目时全部按「会被改名」看待。
    /// <para>
    /// 为 false 的行**不显示预览**（格子里是空的，悬停说明「没被选中」）——
    /// 之前给它们显示「同类全序」的参考值，结果调一下批量序号这些行的预览也跟着变，
    /// 看起来像整张列表都要改名，容易误判。
    /// </para>
    /// </summary>
    public bool InRenameScope
    {
        get => _inRenameScope;
        private set => SetProperty(ref _inRenameScope, value);
    }

    /// <summary>
    /// 鼠标悬停在「预览」列上时的提示。列宽有限、长文件名会被裁切，
    /// 悬停看「原名 → 新名」能确认到底改成了什么。没有预览时返回 null（WPF 不弹提示框）。
    /// </summary>
    public string? PreviewTip
    {
        get
        {
            if (IsParentEntry) return null;

            // 没被选中的行**不显示预览**（见 N115ViewModel.RefreshPreview），
            // 格子里是空的，靠悬停说明这里为什么是空的。
            if (!_inRenameScope) return "这一项没被选中，本次不会改名";

            if (string.IsNullOrWhiteSpace(_preview)) return null;

            return _previewError ? "规则参数有误：" + _preview : $"{_name}  →  {_preview}";
        }
    }

    /// <summary>由 <see cref="N115ViewModel"/> 在列表、选中范围或规则变化后写入预览结果。</summary>
    public void ApplyPreview(string text, bool changed, bool isError, bool inRenameScope)
    {
        // 先写各个标志位：Preview 的 setter 会触发 PreviewTip 重算，那里要用到最新的状态
        InRenameScope = inRenameScope;
        PreviewError = isError;
        PreviewChanged = changed;
        Preview = text;

        // Preview 的值可能没变（SetProperty 就不会发通知），但范围标志变了提示文案也要跟着变
        RaisePropertyChanged(nameof(PreviewTip));
    }

    /// <summary>列表里显示的图标资源键（文件夹 / 按扩展名）。伪条目没有图标，界面用「↑」代替。</summary>
    public string IconKey
    {
        get
        {
            if (IsParentEntry) return string.Empty;
            return IsDirectory ? FileIcon.FolderClosed : FileIcon.ForFile(Name);
        }
    }

    /// <summary>造一个「返回上级」行（列表首行，双击即回到上一级）。</summary>
    public static N115ItemViewModel CreateParentEntry() => new()
    {
        Id = string.Empty,
        Name = "返回上级",
        IsDirectory = true,
        IsParentEntry = true,
    };

    public static N115ItemViewModel FromInfo(N115FileInfo info)
    {
        var isDirectory = ResolveIsDirectory(info);

        return new N115ItemViewModel
        {
            Id = ResolveId(info),
            Name = info.Name ?? string.Empty,
            IsDirectory = isDirectory,
            Size = long.TryParse(info.Size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
            UpdateTimeSeconds = ParseUnixSeconds(info.UpdateTime),
        };
    }

    /// <summary>
    /// 把 115 回的时间字段读成 Unix 秒。空值 / 非法值 / 超范围一律算 0（界面显示空白、排序沉底）。
    /// 正常是 10 位秒；万一以后改回 13 位毫秒，这里也会按毫秒折算，不至于显示成 5 万年以后。
    /// </summary>
    private static long ParseUnixSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return 0;
        if (value > 99_999_999_999L) value /= 1000;                        // 13 位 → 毫秒
        return value is > 0 and <= 253_402_300_799L ? value : 0;           // 超出 UnixTimeSeconds 可表示范围就丢弃
    }

    /// <summary>
    /// 文件夹条目：fid 为空、目录 ID 落在 cid 里；文件条目 fid 有值。
    /// 优先用 fc（0=文件夹 / 1=文件），缺失时按 fid 是否为空兜底。
    /// </summary>
    private static bool ResolveIsDirectory(N115FileInfo info)
    {
        if (!string.IsNullOrEmpty(info.Category)) return info.Category == "0";
        return string.IsNullOrEmpty(info.FileId) && !string.IsNullOrEmpty(info.CategoryId);
    }

    private static string ResolveId(N115FileInfo info)
        => !string.IsNullOrEmpty(info.FileId) ? info.FileId! : info.CategoryId ?? string.Empty;

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

/// <summary>面包屑的一节。路径栏里只显示目录名文字，不带图标。</summary>
public sealed class N115CrumbViewModel
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>除首节外都显示分隔符。</summary>
    public bool ShowSeparator { get; init; }
}
