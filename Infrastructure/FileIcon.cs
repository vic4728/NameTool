using System.IO;

namespace NameTool.Infrastructure;

/// <summary>
/// 按文件名（扩展名）给出图标资源键，键名对应 <c>Resources\Icons.xaml</c> 里的 DrawingImage。
/// 图标原始素材在 <c>Images\ico\*.svg</c>，改图标请改 SVG 后重新生成 Icons.xaml。
/// </summary>
public static class FileIcon
{
    public const string FolderClosed = "IconFolderClosed";
    public const string FolderOpen = "IconFolderOpen";
    public const string Generic = "IconGeneric";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // 字幕（本程序会改写内容的文本类）
        [".srt"] = "IconSubtitle",
        [".ass"] = "IconSubtitle",

        // 纯文本
        [".txt"] = "IconText",

        // 音频（仅改名/复制）
        [".mp3"] = "IconAudio",
        [".wav"] = "IconAudio",

        // 视频（仅改名/复制）
        [".mp4"] = "IconVideo",
        [".mkv"] = "IconVideo",
        [".mov"] = "IconVideo",
        [".ts"] = "IconVideo",

        // 文档类（同样只改名，不读内容）
        [".pdf"] = "IconPdf",
        [".doc"] = "IconWord",
        [".docx"] = "IconWord",
        [".wps"] = "IconWord",
        [".rtf"] = "IconWord",
        [".xls"] = "IconSheet",
        [".xlsx"] = "IconSheet",
        [".csv"] = "IconSheet",
    };

    /// <summary>文件：按扩展名取图标；未识别的一律用通用图标。</summary>
    public static string ForFile(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var key) ? key : Generic;
    }
}
