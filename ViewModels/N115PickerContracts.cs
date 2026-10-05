using NameTool.Services.N115;

namespace NameTool.ViewModels;

/// <summary>
/// 「移动到 / 复制到」这两种动作。
/// <para>
/// 用它替代以前直接传中文字符串的写法：中文动词只该用于**显示**，
/// 一旦拿它当存储键（比如记「上次用过的目标目录」），改文案就会让记忆失效。
/// </para>
/// </summary>
public enum N115TransferKind
{
    Move,
    Copy,
}

public static class N115TransferKindExtensions
{
    /// <summary>稳定的存储键（**不要**改成中文，否则老用户的记忆会读不出来）。</summary>
    public static string ToKey(this N115TransferKind kind)
        => kind == N115TransferKind.Move ? N115PickerMemoryStore.MoveKey : N115PickerMemoryStore.CopyKey;

    /// <summary>界面上显示的动词。</summary>
    public static string ToVerb(this N115TransferKind kind)
        => kind == N115TransferKind.Move ? "移动" : "复制";
}

/// <summary>
/// 用户在选择器里选定的目标文件夹。
/// <para>
/// <paramref name="Segments"/> 是到达它的完整层级（id + 名字），
/// <paramref name="PathIds"/> 只是其中的 id 序列（给合法性校验用）。
/// 之所以两个都带：存「上次目标目录」需要**名字**来还原面包屑，
/// 而拿 <c>PathText</c> 按 " &gt; " 去拆是不可靠的（目录名本身就可能含这个串）。
/// </para>
/// </summary>
public sealed record N115PickerResult(
    string Id,
    string Name,
    string PathText,
    IReadOnlyList<string> PathIds,
    IReadOnlyList<N115PickerPathSegment> Segments);

/// <summary>
/// 由宿主窗口实现：弹出「移动到 / 复制到」的目标文件夹选择器。
/// 返回 null 表示用户取消。
/// </summary>
public delegate Task<N115PickerResult?> N115FolderPickerAsync(
    N115Client client,
    IReadOnlyList<(string Id, string Name)> startPath,
    N115TransferKind kind,
    IReadOnlyList<string> forbiddenIds);
