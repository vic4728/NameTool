using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NameTool.Services.N115;

/// <summary>
/// 115 账号凭据。网页版登录成功后拿到的是 4 个 Cookie（UID/CID/SEID/KID），
/// 后续所有 webapi 请求都靠它们鉴权。
/// </summary>
public sealed class N115Credential
{
    public string UID { get; set; } = string.Empty;
    public string CID { get; set; } = string.Empty;
    public string SEID { get; set; } = string.Empty;
    public string KID { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;
    public long UserId { get; set; }
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(UID) &&
        !string.IsNullOrWhiteSpace(CID) &&
        !string.IsNullOrWhiteSpace(SEID);

    [JsonIgnore]
    public string CookieHeader => $"UID={UID};CID={CID};SEID={SEID};KID={KID}";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(UserName) ? $"UID {UID}" : UserName;

    /// <summary>
    /// 解析用户在浏览器里复制出来的 Cookie 串（形如 UID=..; CID=..; SEID=..; KID=..）。
    /// 允许粘贴整行、含换行、键名大小写不一致。
    /// </summary>
    public static N115Credential? ParseCookieHeader(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in raw.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = segment.IndexOf('=');
            if (idx <= 0) continue;
            var key = segment[..idx].Trim();
            var value = segment[(idx + 1)..].Trim().Trim('"');
            if (key.Length == 0 || value.Length == 0) continue;
            map[key] = value;
        }

        var credential = new N115Credential
        {
            UID = map.TryGetValue("UID", out var uid) ? uid : string.Empty,
            CID = map.TryGetValue("CID", out var cid) ? cid : string.Empty,
            SEID = map.TryGetValue("SEID", out var seid) ? seid : string.Empty,
            KID = map.TryGetValue("KID", out var kid) ? kid : string.Empty,
        };

        return credential.IsValid ? credential : null;
    }
}

/// <summary>
/// 凭据落盘（默认 <c>data\115-account.json</c>），下次启动免登录。
///
/// **升级必须不能丢登录态**，所以读写策略刻意不对称：
/// <list type="bullet">
/// <item><b>读</b>：把 <see cref="AppDataPaths.ReadDirectories"/> 里所有候选目录都找一遍，
/// 取 <c>SavedAt</c> 最新的那份。于是「绿色版换了个目录解压」「从只读目录换到可写目录」
/// 这些情况下，旧位置（尤其是每用户目录）里的凭据仍能找回。</item>
/// <item><b>写</b>：写主目录，同时**镜像**到每用户目录（见
/// <see cref="AppDataPaths.MirrorDirectories"/>）。镜像失败只记日志，不影响本次使用。</item>
/// </list>
/// 旧实现只认 exe 同级 <c>data\</c>、且把写入异常整个吞掉 —— 装到 <c>Program Files</c> 时
/// 表现为「这次登录成功、下次又要登录」，还没有任何提示。这是本轮要修掉的根因。
/// </summary>
public sealed class N115CredentialStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>凭据文件名。</summary>
    public const string FileName = "115-account.json";

    /// <summary>显式指定的目录；为 null 时走 <see cref="AppDataPaths"/> 的多目录策略。</summary>
    private readonly string? _explicitDirectory;

    public N115CredentialStore(string? dataDirectory = null)
    {
        _explicitDirectory = string.IsNullOrWhiteSpace(dataDirectory) ? null : dataDirectory;
    }

    /// <summary>主落盘路径（诊断 / 校验用）。</summary>
    public string FilePath => _explicitDirectory is { } dir
        ? Path.Combine(dir, FileName)
        : AppDataPaths.Combine(FileName);

    /// <summary>实际会去搜索的目录列表（校验用）。</summary>
    public IReadOnlyList<string> SearchDirectories => _explicitDirectory is { } dir
        ? [dir]
        : AppDataPaths.ReadDirectories;

    public N115Credential? Load()
    {
        N115Credential? best = null;
        var bestAt = DateTimeOffset.MinValue;

        foreach (var directory in SearchDirectories)
        {
            var candidate = TryLoadFrom(directory);
            if (candidate is null) continue;

            // 多处都有时取最新的那份：镜像写入可能让每用户目录比便携目录更新
            if (best is null || candidate.SavedAt > bestAt)
            {
                best = candidate;
                bestAt = candidate.SavedAt;
            }
        }

        return best;
    }

    public void Save(N115Credential credential)
    {
        if (credential is null) return;

        credential.SavedAt = DateTimeOffset.Now;
        var json = JsonSerializer.Serialize(credential, Options);

        var wrote = false;
        foreach (var directory in WriteDirectories())
        {
            if (TryWriteTo(directory, json)) wrote = true;
        }

        if (!wrote)
        {
            // 以前这里是彻底静默的 —— 用户会遇到「登录成功但下次又要登录」且无从排查。
            // 至少留一条可见记录（stdout 在 GUI 下看不到，但 DebugView / 日志窗口能看到）。
            System.Diagnostics.Debug.WriteLine(
                $"[115] 凭据写入失败：所有候选目录都不可写（主={FilePath}）");
        }
    }

    /// <summary>写入目录：主目录优先，其余作为镜像。</summary>
    private IEnumerable<string> WriteDirectories()
    {
        if (_explicitDirectory is { } dir)
        {
            yield return dir;
            yield break;
        }

        yield return AppDataPaths.PrimaryDirectory;
        foreach (var mirror in AppDataPaths.MirrorDirectories)
        {
            yield return mirror;
        }
    }

    private N115Credential? TryLoadFrom(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            var credential = JsonSerializer.Deserialize<N115Credential>(json, Options);
            return credential is not null && credential.IsValid ? credential : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryWriteTo(string directory, string json)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, FileName), json);
            return true;
        }
        catch
        {
            // 单个目录写不进去不影响其它候选（只读目录是预期内的情况）
            return false;
        }
    }

    /// <summary>清除登录态：**所有**候选位置都要删，否则清完又被旧文件找回来。</summary>
    public void Clear()
    {
        foreach (var directory in SearchDirectories)
        {
            try
            {
                var path = Path.Combine(directory, FileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }

        // 镜像目录也要清：它们不在 SearchDirectories 里（那只是「读」的列表）
        if (_explicitDirectory is null)
        {
            foreach (var directory in AppDataPaths.MirrorDirectories)
            {
                try
                {
                    var path = Path.Combine(directory, FileName);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
