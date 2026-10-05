using System.IO;
using System.Text;
using System.Text.Json;

namespace NameTool.Services;

/// <summary>
/// 一列的宽度记录 —— 只存「能重建 <c>DataGridLength</c>」的最小信息。
/// </summary>
public sealed class ColumnWidthEntry
{
    /// <summary>
    /// 列的表头文字，当键用。
    /// <para>
    /// 用表头而不是下标：以后在中间插一列，老记录不会整体错位（最多是新增那列用默认宽度）。
    /// </para>
    /// </summary>
    public string Header { get; set; } = string.Empty;

    /// <summary>宽度值：<c>Pixel</c> 时是像素数，<c>Star</c> 时是星数；其余单位固定为 1。</summary>
    public double Value { get; set; } = 1;

    /// <summary>Pixel / Star / Auto / SizeToCells / SizeToHeader。</summary>
    public string Unit { get; set; } = "Pixel";
}

/// <summary>
/// 列宽的落盘仓库：<c>data\column-widths.json</c>，形如
/// <code>
/// {
///   "local": [ { "header": "参与", "value": 52, "unit": "Pixel" } ],
///   "n115":  [ { "header": "名称", "value": 2.0, "unit": "Star" } ]
/// }
/// </code>
/// <para>
/// 与「批量序号预设」共用同一个数据目录（exe 同级 <c>data\</c>），所以绿色版和安装版各存各的，
/// 不会互相覆盖。可用环境变量 <see cref="SettingsDirEnvVar"/> 覆盖目录：离线校验靠它把读写隔离到
/// 临时目录，免得每跑一轮校验就往自己的产物目录里写，下一轮又把这堆数据读回来当真实布局用。
/// </para>
/// <para>
/// 任何读写失败都静默吞掉：列宽只是使用习惯，读不到就用 XAML 里的默认值，绝不能因此起不来。
/// </para>
/// </summary>
public static class ColumnWidthStore
{
    /// <summary>落盘文件名。</summary>
    public const string FileName = "column-widths.json";

    /// <summary>覆盖数据目录的环境变量名（与 <see cref="AppDataPaths.DirEnvVar"/> 同一个）。</summary>
    public const string SettingsDirEnvVar = AppDataPaths.DirEnvVar;

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 刻意不开 PropertyNameCaseInsensitive：自己写的文件格式固定，宽容只会掩盖拼写错误。
    };

    /// <summary>exe 同级 <c>data\</c>（绿色版形态）。</summary>
    public static string PortableDirectory => AppDataPaths.PortableDirectory;

    /// <summary>
    /// 兜底目录 <c>%LOCALAPPDATA%\NameTool</c>。安装版落在 <c>D:\Program Files\NameTool</c> 时
    /// 普通用户通常写不进去，写不进就等于「列宽记不住」——而记不住恰恰让这个功能失去意义。
    /// </summary>
    public static string FallbackDirectory => AppDataPaths.PerUserDirectory;

    /// <summary>
    /// 列宽记录目录。解析逻辑（含可写性兜底）已统一到 <see cref="AppDataPaths"/>，
    /// 这里只做转发 —— 以前是各写一套，结果「登录态」和「列宽」两个功能的健壮性不一样。
    /// </summary>
    public static string StoreDirectory => AppDataPaths.PrimaryDirectory;

    /// <summary>落盘文件的完整路径（诊断 / 校验用）。</summary>
    public static string StorePath => Path.Combine(StoreDirectory, FileName);

    /// <summary>读某个列表的列宽记录；没有或读坏了都返回 null（调用方回落默认宽度）。</summary>
    public static List<ColumnWidthEntry>? Load(string gridKey)
    {
        if (string.IsNullOrWhiteSpace(gridKey)) return null;

        lock (Gate)
        {
            var all = ReadAllUnlocked();
            return all.TryGetValue(gridKey, out var entries) && entries is { Count: > 0 } ? entries : null;
        }
    }

    /// <summary>写入某个列表的列宽记录（保留文件里其它列表的记录）。</summary>
    public static void Save(string gridKey, List<ColumnWidthEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(gridKey) || entries is null || entries.Count == 0) return;

        lock (Gate)
        {
            var all = ReadAllUnlocked();
            all[gridKey] = entries;
            WriteAllUnlocked(all);
        }
    }

    /// <summary>删掉某个列表的记录，并立即落盘（「重置列宽」用它，下次启动就是默认布局）。</summary>
    public static bool Remove(string gridKey)
    {
        if (string.IsNullOrWhiteSpace(gridKey)) return false;

        lock (Gate)
        {
            var all = ReadAllUnlocked();
            if (!all.Remove(gridKey)) return false;

            // 一个都不剩就直接删文件，别留个 {} 在那儿让人以为还有记录。
            // 所有候选目录都删：留一份在别处，下次启动又会被当作记录读回来。
            if (all.Count == 0)
            {
                foreach (var directory in AppDataPaths.ReadDirectories)
                {
                    TryDeleteUnlocked(Path.Combine(directory, FileName));
                }
            }
            else
            {
                WriteAllUnlocked(all);
            }

            return true;
        }
    }

    /// <summary>清空整份记录（诊断用）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            TryDeleteUnlocked(StorePath);
        }
    }

    private static Dictionary<string, List<ColumnWidthEntry>> ReadAllUnlocked()
    {
        try
        {
            if (!File.Exists(StorePath)) return new Dictionary<string, List<ColumnWidthEntry>>(StringComparer.Ordinal);

            var json = File.ReadAllText(StorePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, List<ColumnWidthEntry>>(StringComparer.Ordinal);

            var all = JsonSerializer.Deserialize<Dictionary<string, List<ColumnWidthEntry>>>(json, JsonOptions);
            return all is null
                ? new Dictionary<string, List<ColumnWidthEntry>>(StringComparer.Ordinal)
                : new Dictionary<string, List<ColumnWidthEntry>>(all, StringComparer.Ordinal);
        }
        catch
        {
            // 文件坏了就当没有 —— 一份列宽不值得拦住启动
            return new Dictionary<string, List<ColumnWidthEntry>>(StringComparer.Ordinal);
        }
    }

    private static void WriteAllUnlocked(Dictionary<string, List<ColumnWidthEntry>> all)
    {
        // 先试主目录；写不进去（只读目录等）就把这次改动落到兜底目录 ——
        // 注意不是「搬目录」，<see cref="AppDataPaths"/> 在解析主目录时已经做过可写性探测，
        // 走到这里说明探测结论过期了（例如装完程序后目录权限变了）。重试一次即可，不做循环。
        if (TryWriteUnlocked(all, StoreDirectory)) return;

        if (AppDataPaths.EnvOverride is null
            && !string.Equals(StoreDirectory, FallbackDirectory, StringComparison.OrdinalIgnoreCase))
        {
            TryWriteUnlocked(all, FallbackDirectory);
        }
    }

    private static bool TryWriteUnlocked(Dictionary<string, List<ColumnWidthEntry>> all, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // 先写临时文件再替换：中途断电不会留下一份半截 JSON（半截正好会被上面的 catch 当成"没有"）
            var target = Path.Combine(directory, FileName);
            var tmp = target + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOptions), new UTF8Encoding(false));

            if (File.Exists(target))
            {
                File.Replace(tmp, target, null);
            }
            else
            {
                File.Move(tmp, target);
            }

            return true;
        }
        catch
        {
            // 写不进去也不影响本次使用，只是下次回到默认列宽
            return false;
        }
    }

    private static void TryDeleteUnlocked(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
