using System.IO;
using System.Text;
using System.Text.Json;

namespace NameTool.Services;

/// <summary>
/// 界面使用习惯的落盘仓库（当前：功能区各分组的展开 / 收起状态）：<c>data\ui-state.json</c>，形如
/// <code>
/// { "section.add-del-filename": true, "section.delete-lines": false }
/// </code>
/// <para>
/// 与列宽记录共用同一个数据目录（exe 同级 <c>data\</c>，写不进兜底 <c>%LOCALAPPDATA%\NameTool</c>），
/// 环境变量 <see cref="AppDataPaths.DirEnvVar"/> 可覆盖目录 —— 离线校验靠它把读写隔离到临时目录。
/// </para>
/// <para>
/// 任何读写失败都静默吞掉：展开状态只是使用习惯，读不到就回到代码里的默认值，绝不能因此起不来。
/// </para>
/// </summary>
public static class UiStateStore
{
    /// <summary>落盘文件名。</summary>
    public const string FileName = "ui-state.json";

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>exe 同级 <c>data\</c>（绿色版形态）；写不进由 <see cref="AppDataPaths"/> 兜底到每用户目录。</summary>
    private static string StoreDirectory => AppDataPaths.PrimaryDirectory;

    private static string StorePath => Path.Combine(StoreDirectory, FileName);

    /// <summary>读一个布尔状态；没有或读坏了返回 <paramref name="fallback"/>。</summary>
    public static bool GetBool(string key, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(key)) return fallback;

        lock (Gate)
        {
            var all = ReadAllUnlocked();
            return all.TryGetValue(key, out var value) ? value : fallback;
        }
    }

    /// <summary>写一个布尔状态并立即落盘（保留文件里其它键）。</summary>
    public static void SetBool(string key, bool value)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        lock (Gate)
        {
            var all = ReadAllUnlocked();
            all[key] = value;
            WriteAllUnlocked(all);
        }
    }

    private static Dictionary<string, bool> ReadAllUnlocked()
    {
        try
        {
            if (!File.Exists(StorePath)) return new Dictionary<string, bool>(StringComparer.Ordinal);

            var json = File.ReadAllText(StorePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, bool>(StringComparer.Ordinal);

            var all = JsonSerializer.Deserialize<Dictionary<string, bool>>(json, JsonOptions);
            return all is null
                ? new Dictionary<string, bool>(StringComparer.Ordinal)
                : new Dictionary<string, bool>(all, StringComparer.Ordinal);
        }
        catch
        {
            // 文件坏了就当没有 —— 一份展开状态不值得拦住启动
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }
    }

    private static void WriteAllUnlocked(Dictionary<string, bool> all)
    {
        try
        {
            Directory.CreateDirectory(StoreDirectory);

            // 先写临时文件再替换：中途断电不会留下半截 JSON（半截正好被上面的 catch 当成"没有"）
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOptions), new UTF8Encoding(false));

            if (File.Exists(StorePath))
            {
                File.Replace(tmp, StorePath, null);
            }
            else
            {
                File.Move(tmp, StorePath);
            }
        }
        catch
        {
            // 写不进去也不影响本次使用，只是下次回到默认状态
        }
    }
}
