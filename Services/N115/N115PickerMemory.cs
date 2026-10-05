using System.IO;
using System.Text.Json;

namespace NameTool.Services.N115;

/// <summary>路径上的一节（目录 id + 显示名）。选择器用它还原完整面包屑。</summary>
public sealed record N115PickerPathSegment(string Id, string Name);

/// <summary>
/// 「移动到 / 复制到」选择器的**目标目录记忆**。
///
/// 用途：窗口下次打开时直接回到上次用过的那个目录，省掉「每次都要从当前目录重新点进去」。
/// 按动作**分开记**（移动一份、复制一份）—— 这两个动作的常用目标通常不是同一处
/// （比如「复制到」常去素材库、「移动到」常去归档目录），共用一个记忆会互相打乱。
///
/// 存的是**完整路径链**而不只是最后一级 id：这样面包屑能一次还原到位，
/// 用户点上面任意一节都能跳回去，不用逐层加载。
///
/// 落在 <see cref="AppDataPaths.PrimaryDirectory"/> 下，与其它用户数据同一处
/// （升级不丢，见 MEMORY-RELEASE）。
/// </summary>
public sealed class N115PickerMemoryStore
{
    /// <summary>落盘文件名。</summary>
    public const string FileName = "picker-memory.json";

    /// <summary>「移动」的记忆键。</summary>
    public const string MoveKey = "move";

    /// <summary>「复制」的记忆键。</summary>
    public const string CopyKey = "copy";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string? _explicitDirectory;

    public N115PickerMemoryStore(string? directory = null)
        => _explicitDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory;

    /// <summary>落盘路径（诊断 / 校验用）。</summary>
    public string FilePath => Path.Combine(
        _explicitDirectory ?? AppDataPaths.PrimaryDirectory, FileName);

    /// <summary>
    /// 读出某个动作上次用过的路径链。没有记录（或文件损坏）返回 null ——
    /// 调用方据此回退到默认起点。**"记忆损坏"不该影响功能可用性**，所以一概吞掉异常当没记过。
    /// </summary>
    public IReadOnlyList<N115PickerPathSegment>? Load(string key)
    {
        var all = ReadAll();
        if (!all.TryGetValue(key, out var path) || path is null || path.Count == 0) return null;

        // 过滤掉空条目：id/名字缺失的节还原出来只会让面包屑出现空白按钮
        var clean = path
            .Where(s => s is not null
                        && !string.IsNullOrWhiteSpace(s.Id)
                        && !string.IsNullOrWhiteSpace(s.Name))
            .ToList();

        return clean.Count == 0 ? null : clean;
    }

    /// <summary>记下某个动作这次用的完整路径链。</summary>
    public void Save(string key, IReadOnlyList<N115PickerPathSegment> path)
    {
        if (string.IsNullOrWhiteSpace(key) || path.Count == 0) return;

        var all = ReadAll();
        all[key] = path
            .Where(s => s is not null
                        && !string.IsNullOrWhiteSpace(s.Id)
                        && !string.IsNullOrWhiteSpace(s.Name))
            .ToList();

        Write(all);
    }

    /// <summary>忘掉某个动作的记忆（目标目录已经失效时用）。</summary>
    public void Clear(string key)
    {
        var all = ReadAll();
        if (all.Remove(key)) Write(all);
    }

    /// <summary>清掉全部记忆（诊断用）。</summary>
    public void ClearAll()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // 删不掉就当没有，不值得打断用户操作
        }
    }

    private Dictionary<string, List<N115PickerPathSegment>> ReadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Dictionary<string, List<N115PickerPathSegment>>(StringComparer.Ordinal);

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, List<N115PickerPathSegment>>(StringComparer.Ordinal);

            var all = JsonSerializer.Deserialize<Dictionary<string, List<N115PickerPathSegment>>>(json, Options);
            return all is null
                ? new Dictionary<string, List<N115PickerPathSegment>>(StringComparer.Ordinal)
                : new Dictionary<string, List<N115PickerPathSegment>>(all, StringComparer.Ordinal);
        }
        catch
        {
            // 文件坏了就当没记过 —— 一份导航记忆不值得拦住用户
            return new Dictionary<string, List<N115PickerPathSegment>>(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, List<N115PickerPathSegment>> all)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            // 先写临时文件再替换：中途出错不会留下半截 JSON（半截正好会被上面的 catch 当成"没有"）
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // 落盘失败不影响本次选择，只是下次不记得
        }
    }
}
