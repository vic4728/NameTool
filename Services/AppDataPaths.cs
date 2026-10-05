using System.IO;

namespace NameTool.Services;

/// <summary>
/// 应用数据目录的**单一来源**。
///
/// 背景（v1.9.0 之前是三套各自为政的写法，导致「升级后要重新登录 115」）：
/// <list type="bullet">
/// <item>115 凭据、序号预设写 <c>{exe}\data</c>，**没有兜底** —— 安装版装到
/// <c>D:\Program Files\NameTool</c> 时普通用户写不进去，<c>Save</c> 里那个 catch 把异常吞掉，
/// 表现为「这次登录成功、下次打开又要登录」，且完全没有任何提示。</item>
/// <item>列宽记录也写 <c>{exe}\data</c>，但自己有兜底（v1.8.2 加的），与上面不一致。</item>
/// <item>内嵌浏览器的数据目录写在 <c>%LOCALAPPDATA%\NameTool\115webview</c>，天然跨升级保留 ——
/// 于是出现「浏览器里的登录还在、程序却认为自己没凭据」的割裂状态。</item>
/// </list>
///
/// 现在统一成一处解析，并且**读取时搜索全部候选、写入时镜像到每用户目录**：
/// <list type="number">
/// <item><b>主目录</b>：环境变量 → exe 同级 <c>data\</c>（可写就用，保住绿色版的便携语义）
/// → 否则 <c>%LOCALAPPDATA%\NameTool</c>。写不进去的时候不会静默失败。</item>
/// <item><b>读取</b>：主目录 → exe 同级 → 每用户 → 历史遗留位置，逐个找。
/// 这样「绿色版换了个目录解压」也能从每用户目录把凭据找回来。</item>
/// <item><b>镜像写入</b>：除主目录外再往每用户目录写一份。凭据因此有了一个与 exe 位置无关的
/// 长期落点，任何升级方式都带不走它。</item>
/// </list>
///
/// 关于「便携版也会往 %LOCALAPPDATA% 写一份」的取舍：这与内嵌浏览器的行为一致
/// （它的会话数据本来就无条件存在那里，含 Cookie），并没有引入新的暴露面，
/// 换来的是「换目录 / 覆盖安装 / 装到只读目录」三种升级方式都不会丢登录态。
/// </summary>
public static class AppDataPaths
{
    /// <summary>覆盖数据目录的环境变量（离线校验靠它把读写隔离到临时目录）。</summary>
    public const string DirEnvVar = "NAMETOOL_SETTINGS_DIR";

    /// <summary>每用户目录名（<c>%LOCALAPPDATA%\NameTool</c>）。</summary>
    public const string FolderName = "NameTool";

    /// <summary>便携数据目录名（exe 同级）。</summary>
    public const string DataFolderName = "data";

    private static readonly object Gate = new();

    private static string? _primary;
    private static IReadOnlyList<string>? _reads;
    private static IReadOnlyList<string>? _mirrors;
    private static bool? _portableWritable;

    /// <summary>
    /// 仅供离线校验：把「便携根」「每用户根」换到临时目录。
    /// <para>
    /// 为什么需要这个接缝：要验证的是「换目录 / 只读目录 / 镜像」这些**跨目录**行为，
    /// 而默认根目录一个是校验产物目录、另一个是**用户的真实</b> <c>%LOCALAPPDATA%</c> ——
    /// 往那里写凭据显然不行。有了这个覆盖，整条链路可以在临时目录里完整跑一遍。
    /// 生产代码从不调用它（<c>val</c> 用完就 <see cref="ClearRootOverrides"/>）。
    /// </para>
    /// </summary>
    private static string? _portableRootOverride;
    private static string? _perUserRootOverride;

    /// <summary>便携位置：exe 同级 <c>data\</c>。绿色版把数据跟着 exe 一起带走。</summary>
    public static string PortableDirectory =>
        Path.Combine(_portableRootOverride ?? AppContext.BaseDirectory, DataFolderName);

    /// <summary>每用户位置 <c>%LOCALAPPDATA%\NameTool</c>。装到只读目录时的兜底，也是跨升级的长期落点。</summary>
    public static string PerUserDirectory => _perUserRootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>
    /// 内嵌浏览器（WebView2）的数据目录。一直放在每用户目录下：
    /// 它体积大、且本来就是「跟着 Windows 用户走」的浏览器会话数据，
    /// 放在这里跨升级天然保留（这也是它一直没出问题的原因）。
    /// </summary>
    public static string WebViewDirectory => Path.Combine(PerUserDirectory, "115webview");

    /// <summary>环境变量强制指定的目录；为空表示没有覆盖。</summary>
    public static string? EnvOverride
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(DirEnvVar);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    /// <summary>
    /// 主目录（**新数据写到这里**）。优先级：环境变量 → exe 同级（可写）→ 每用户。
    /// </summary>
    public static string PrimaryDirectory
    {
        get
        {
            if (EnvOverride is { } env) return env;   // 强制指定时不兜底：「说在哪就在哪」
            if (_primary is not null) return _primary;

            lock (Gate)
            {
                if (_primary is not null) return _primary;

                // 已经有文件的那个便携目录优先用（曾在便携模式下写过，就继续写那里，
                // 免得读了 A 又写去 B，出现两份会互相矛盾的记录）
                if (HasAnyFile(PortableDirectory)) return _primary = PortableDirectory;

                _primary = IsPortableWritable ? PortableDirectory : PerUserDirectory;
                return _primary;
            }
        }
    }

    /// <summary>exe 同级的 <c>data\</c> 是否可写（每个进程只探一次）。</summary>
    public static bool IsPortableWritable
    {
        get
        {
            if (_portableWritable is { } cached) return cached;

            lock (Gate)
            {
                if (_portableWritable is { } again) return again;

                _portableWritable = CanWriteTo(PortableDirectory);
                return _portableWritable.Value;
            }
        }
    }

    /// <summary>当前是否处于便携模式（数据写在 exe 同级）。</summary>
    public static bool IsPortable =>
        string.Equals(PrimaryDirectory, PortableDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读取时按顺序搜索的目录。<b>环境变量指定时只搜那一个</b> ——
    /// 离线校验依赖这个语义，否则会把真实用户数据读进来污染断言。
    /// </summary>
    public static IReadOnlyList<string> ReadDirectories
    {
        get
        {
            if (_reads is not null) return _reads;

            lock (Gate)
            {
                if (_reads is not null) return _reads;

                if (EnvOverride is { } env)
                {
                    return _reads = [env];
                }

                var list = new List<string> { PrimaryDirectory };
                AddIfNew(list, PortableDirectory);
                AddIfNew(list, PerUserDirectory);

                // 历史遗留位置（老版本用过），保证从很旧的版本升上来也能找回登录态
                AddIfNew(list, Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName));
                AddIfNew(list, Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName, DataFolderName));
                AddIfNew(list, Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName));

                return _reads = list;
            }
        }
    }

    /// <summary>
    /// 除主目录外**还要镜像一份**的目录。让凭据有一个与 exe 位置无关的长期落点，
    /// 覆盖「绿色版换目录解压」「覆盖安装」「装到 Program Files」三种升级方式。
    /// </summary>
    public static IReadOnlyList<string> MirrorDirectories
    {
        get
        {
            if (_mirrors is not null) return _mirrors;

            lock (Gate)
            {
                if (_mirrors is not null) return _mirrors;

                // 环境变量指定时不做镜像：校验要的是「写哪儿就读哪儿」，多写一份反而会污染真实目录
                if (EnvOverride is not null) return _mirrors = [];

                var list = new List<string>();
                if (IsPortableWritable) AddIfNew(list, PortableDirectory);
                AddIfNew(list, PerUserDirectory);
                list.RemoveAll(d => string.Equals(d, PrimaryDirectory, StringComparison.OrdinalIgnoreCase));

                return _mirrors = list;
            }
        }
    }

    /// <summary>拼一个数据文件的完整路径（主目录下）。</summary>
    public static string Combine(string fileName) => Path.Combine(PrimaryDirectory, fileName);

    /// <summary>清掉解析缓存（离线校验里改过环境变量后需要）。</summary>
    public static void ResetCache()
    {
        lock (Gate)
        {
            _primary = null;
            _reads = null;
            _mirrors = null;
            _portableWritable = null;
        }
    }

    /// <summary>
    /// 仅供离线校验：把两个根目录换到临时位置，用来演练「换目录解压」「装到只读目录」
    /// 「镜像写入」这些跨目录场景。**生产代码不要调用。**
    /// </summary>
    /// <param name="portableRoot">代替 exe 所在目录（其下会拼 <c>data\</c>）。</param>
    /// <param name="perUserRoot">代替 <c>%LOCALAPPDATA%\NameTool</c>。</param>
    public static void OverrideRoots(string? portableRoot, string? perUserRoot)
    {
        lock (Gate)
        {
            _portableRootOverride = string.IsNullOrWhiteSpace(portableRoot) ? null : portableRoot;
            _perUserRootOverride = string.IsNullOrWhiteSpace(perUserRoot) ? null : perUserRoot;
            _primary = null;
            _reads = null;
            _mirrors = null;
            _portableWritable = null;
        }
    }

    /// <summary>撤销 <see cref="OverrideRoots"/> 并清缓存。同样只给离线校验用。</summary>
    public static void ClearRootOverrides()
    {
        lock (Gate)
        {
            _portableRootOverride = null;
            _perUserRootOverride = null;
            _primary = null;
            _reads = null;
            _mirrors = null;
            _portableWritable = null;
        }
    }

    /// <summary>尽力建目录；失败返回 false（调用方通常无需处理，写文件时还会有一次机会）。</summary>
    public static bool TryEnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddIfNew(List<string> list, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        if (list.Any(x => string.Equals(x, directory, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(directory);
    }

    private static bool HasAnyFile(string directory)
    {
        try
        {
            return Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>落一个小文件试探可写性（每个进程只探一次）。</summary>
    private static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
