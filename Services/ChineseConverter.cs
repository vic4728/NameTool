using System.IO;
using System.Reflection;
using System.Text;

namespace NameTool.Services;

/// <summary>
/// 繁体 → 简体转换（「繁=>简」按钮的内核）。
/// <para>
/// 词典是**内嵌资源** <c>Resources\t2s-dict.txt</c>：主表取自 HanLP 的
/// <c>data/dictionary/tc/t2s.txt</c>（用户指定的 quick-chinese-transfer 项目词典即来自此处），
/// 另用 OpenCC 的 TSCharacters / TSPhrases 补齐主表没有的 349 个键，共约 4800 条。
/// 以嵌入资源而非散落文件发布，绿色单文件形态不受影响。
/// </para>
/// <para>
/// 算法是**最长匹配优先**（不是逐字替换），所以「乾隆」不会被单字规则 <c>乾=干</c> 误转成「干隆」——
/// 词典里刻意保留了 <c>乾隆=乾隆</c> 这类「不变条目」，它们的作用就是保护词语不被拆开。
/// </para>
/// </summary>
public static class ChineseConverter
{
    private const string DictFileName = "t2s-dict.txt";

    /// <summary>词典里最长的键是 14 个汉字；超长键直接忽略，防止恶意/损坏的词典拖慢匹配。</summary>
    private const int MaxKeyLength = 16;

    private static int _entryCount = -1;

    /// <summary>首字 → 候选条目（按键长度倒序），首次访问时加载词典。</summary>
    private static readonly Lazy<Dictionary<char, List<KeyValuePair<string, string>>>> Table =
        new(BuildTable, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>词典条目数（懒加载；用于启动自检与离线断言）。</summary>
    public static int EntryCount
    {
        get
        {
            _ = Table.Value;
            return _entryCount;
        }
    }

    /// <summary>词典是否已加载。</summary>
    public static bool IsLoaded => Table.IsValueCreated;

    /// <summary>提前把词典读进来，避免首次点「繁=>简」时卡一下。可放在后台线程调用。</summary>
    public static void WarmUp() => _ = Table.Value;

    private static Dictionary<char, List<KeyValuePair<string, string>>> BuildTable()
    {
        var assembly = typeof(ChineseConverter).Assembly;

        // 按后缀找资源名，不写死 "NameTool.Resources.t2s-dict.txt"：
        // 万一以后改了 RootNamespace，这里不会静默失效。
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith(DictFileName, StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new InvalidOperationException(
                $"内嵌词典 {DictFileName} 缺失：NameTool.csproj 里需要 <EmbeddedResource Include=\"Resources\\{DictFileName}\" />");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var table = new Dictionary<char, List<KeyValuePair<string, string>>>();
        var count = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;

            var sep = line.IndexOf('=');
            if (sep <= 0 || sep == line.Length - 1) continue;

            var key = line[..sep];
            if (key.Length > MaxKeyLength) continue;

            if (!table.TryGetValue(key[0], out var candidates))
            {
                table[key[0]] = candidates = [];
            }

            candidates.Add(new KeyValuePair<string, string>(key, line[(sep + 1)..]));
            count++;
        }

        // 长的键排前面 = 最长匹配优先
        foreach (var candidates in table.Values)
        {
            candidates.Sort(static (a, b) => b.Key.Length - a.Key.Length);
        }

        _entryCount = count;
        return table;
    }

    /// <summary>
    /// 把任意文本里的繁体字转成简体。已是简体的内容基本原样返回（幂等），
    /// ASCII、数字、标点一律不动。
    /// </summary>
    public static string ToSimplified(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var table = Table.Value;
        var result = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            if (!table.TryGetValue(text[i], out var candidates))
            {
                // 没有以这个字开头的词条：连续拷贝一段，避免逐字 Append
                var start = i;
                while (i < text.Length && !table.ContainsKey(text[i])) i++;
                result.Append(text, start, i - start);
                continue;
            }

            var matched = false;
            foreach (var (key, value) in candidates)
            {
                if (key.Length <= text.Length - i
                    && string.CompareOrdinal(text, i, key, 0, key.Length) == 0)
                {
                    result.Append(value);
                    i += key.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                // 只出现在词语里的字：落单时保持原样
                result.Append(text[i]);
                i++;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// 转换文件名：**扩展名原样保留**（只转主干），文件夹则整名都转
    /// （文件夹没有扩展名，<c>Path.GetExtension("2024.01 素材")</c> 会错误地切出「.01 素材」）。
    /// </summary>
    public static string ToSimplifiedFileName(string? name, bool isDirectory)
    {
        if (string.IsNullOrEmpty(name)) return name ?? string.Empty;
        if (isDirectory) return ToSimplified(name);

        var ext = Path.GetExtension(name);
        if (ext.Length == 0) return ToSimplified(name);

        var stem = name[..^ext.Length];
        return ToSimplified(stem) + ext;
    }
}
