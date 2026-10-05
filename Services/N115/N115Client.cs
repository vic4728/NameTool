using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace NameTool.Services.N115;

/// <summary>115 接口返回的业务错误。</summary>
public sealed class N115ApiException : Exception
{
    public N115ApiException(string message, int errno = 0, bool isAuthError = false)
        : base(message)
    {
        Errno = errno;
        IsAuthError = isAuthError;
    }

    public int Errno { get; }

    /// <summary>登录态失效，需要重新登录授权（网页登录 / 扫码 / 填 Cookie）。</summary>
    public bool IsAuthError { get; }
}

/// <summary>凭据探测结论。登录态的死活必须区分「确认失效」与「暂时无法确认」。</summary>
public enum N115ProbeOutcome
{
    /// <summary>接口明确返回 state:true —— 登录态有效。</summary>
    Valid,

    /// <summary>接口明确拒绝（990001「登录超时」/ 401 段）—— 登录态确实失效了。</summary>
    AuthExpired,

    /// <summary>网络失败 / WAF 拦截 / 响应异常 —— 证明不了登录态的死活，
    /// **不该据此要求用户重新登录**（旧实现就是把这类失败一律当失效）。</summary>
    Inconclusive,
}

/// <summary>
/// 115 网页版接口客户端（个人账号 + 网页登录/扫码授权，不依赖开放平台开发者资质）。
///
/// 登录链路（与 115 网页端一致）：
///   1. GET  qrcodeapi.115.com/api/1.0/web/1.0/token        取二维码内容 + uid/time/sign
///   2. GET  qrcodeapi.115.com/get/status/  (长轮询)         0 待扫码 / 1 已扫码 / 2 已确认
///   3. POST passportapi.115.com/app/1.0/web/1.0/login/qrcode  account=uid&app=web 换 Cookie
/// 拿到 UID/CID/SEID/KID 之后，目录列表与改名都走 webapi.115.com。
/// </summary>
public sealed class N115Client : IDisposable
{
    public const string ApiFileList = "https://webapi.115.com/files";
    public const string ApiFileRename = "https://webapi.115.com/files/batch_rename";
    public const string ApiFileMove = "https://webapi.115.com/files/move";
    public const string ApiFileCopy = "https://webapi.115.com/files/copy";
    public const string ApiFileDelete = "https://webapi.115.com/rb/delete";

    private const string ApiQrToken = "https://qrcodeapi.115.com/api/1.0/web/1.0/token";
    private const string ApiQrStatus = "https://qrcodeapi.115.com/get/status/";
    private const string ApiQrLogin = "https://passportapi.115.com/app/1.0/web/1.0/login/qrcode";
    /// <summary>
    /// 登录态探测端点。⚠️ 旧的 <c>my.115.com/?ct=guide&amp;ac=status</c> 已于 2026-10-04 前后下线
    /// （实测返回的是跳转 115.com 首页的 HTML，不再是 JSON）。
    /// 继续用它会把「探测失败」误判成「登录失效」——用户每次打开网盘都被要求重新授权，
    /// 而且授权窗口用同一端点做最终确认，**重新授权永远无法完成**（「每次升级后都要重新认证」的真正元凶）。
    /// user/info 实测仍返回规范 JSON：失效时 {"state":false,"errNo":990001,"error":"登录超时，请重新登录。"}。
    /// </summary>
    public const string ApiCredentialProbe = "https://webapi.115.com/user/info";
    private const string ApiUserInfo = "https://my.115.com/?ct=ajax&ac=nav";

    private const string UserAgent = "Mozilla/5.0 115Browser/27.0.5.7";

    /// <summary>单页最大条数（115 服务端上限 1150）。</summary>
    public const int MaxPageSize = 1150;

    /// <summary>
    /// 两次 115 请求之间的最小间隔。
    /// 实测：短时间内的密集请求（累计约两百次）会让 115 侧的 WAF 对 `/files` 接口返回 HTTP 405，
    /// 并持续 20 分钟以上都不恢复，期间所有目录都打不开。所以列表/改名请求都走统一节流。
    /// </summary>
    private const int MinRequestIntervalMs = 250;

    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    private static async Task ThrottleAsync(CancellationToken ct)
    {
        await RequestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = TimeSpan.FromMilliseconds(MinRequestIntervalMs) - (DateTimeOffset.UtcNow - _lastRequestAt);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    /// <summary>115 在限流或服务波动时返回的状态码 —— 这类失败值得隔一会儿重试。</summary>
    private static bool IsTransient(HttpStatusCode status)
        => status == HttpStatusCode.MethodNotAllowed       // 405：实测就是 115 的限流表现
           || status == HttpStatusCode.TooManyRequests    // 429
           || status == HttpStatusCode.RequestTimeout     // 408
           || (int)status >= 500;

    private static string DescribeTransient(HttpStatusCode status) => (int)status switch
    {
        405 => "115 暂时限制了取目录列表的访问（HTTP 405）。实测这类阻断会持续二十分钟以上，"
               + "请停止所有操作耐心等待——反复刷新或连续切换目录只会让它更久。"
               + "这与登录失效无关，不需要重新登录。",
        429 => "115 提示请求过于频繁（HTTP 429），请稍后重试。",
        408 => "115 响应超时（HTTP 408），请稍后重试。",
        _ => $"115 服务器暂时不可用（HTTP {(int)status}），请稍后重试。",
    };

    private static TimeSpan RetryBackoff(int attempt) => TimeSpan.FromMilliseconds(700 * attempt * attempt);

    /// <summary>
    /// 注意：这里刻意保持大小写敏感。115 的成功响应里同时有 errno 和 errNo 两个字段，
    /// 一旦开大小写不敏感匹配，两个属性会撞名导致反序列化直接抛异常。
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private bool _disposed;

    public N115Client()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookies,
            AutomaticDecompression = DecompressionMethods.All,
        };

        _http = new HttpClient(handler)
        {
            // 状态查询是长轮询，不能吃 HttpClient 的默认超时；改为按请求传 CancellationToken
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://115.com/");
    }

    public N115Credential? Credential { get; private set; }

    private static long NowMilli() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string Describe(N115BasicResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Error)) return response.Error!;
        if (!string.IsNullOrWhiteSpace(response.Msg)) return response.Msg!;
        if (!string.IsNullOrWhiteSpace(response.Errno)) return $"errno={response.Errno}";
        if (response.ErrNo != 0) return $"errNo={response.ErrNo}";
        return "接口返回失败";
    }

    private static int ParseErrno(N115BasicResponse response)
        => int.TryParse(response.Errno, out var value) ? value : response.ErrNo;

    /// <summary>115 登录态失效的常见 errno：990001「登录超时」以及 401 段。</summary>
    private static bool LooksLikeAuthError(int errno)
        => errno == 990001
           || (errno >= 40101000 && errno <= 40101999);

    private static void ThrowIfFailed(N115BasicResponse response)
    {
        if (response.State) return;

        var errno = ParseErrno(response);
        var message = Describe(response);
        var isAuth = LooksLikeAuthError(errno)
                     || message.Contains("登录", StringComparison.Ordinal)
                     || message.Contains("未登录", StringComparison.Ordinal);

        throw new N115ApiException(
            isAuth ? "115 登录状态已失效，请重新登录" : $"115 接口错误：{message}",
            errno,
            isAuth);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
        where T : class
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new N115ApiException($"115 返回空响应（HTTP {(int)response.StatusCode}）");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                   ?? throw new N115ApiException("115 返回内容无法解析");
        }
        catch (JsonException)
        {
            throw new N115ApiException($"115 返回了非预期内容（HTTP {(int)response.StatusCode}），可能是登录态失效或接口调整");
        }
    }

    /// <summary>
    /// 带节流与重试的 GET + JSON 解析。跳过 115 的限流/波动状态码并稍后重试，
    /// 重试仍失败时给出可直接照做的中文提示。
    /// </summary>
    private async Task<T> GetJsonWithRetryAsync<T>(string url, CancellationToken ct) where T : class
    {
        const int maxAttempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await ThrottleAsync(ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (IsTransient(response.StatusCode))
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(RetryBackoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }

                throw new N115ApiException(
                    DescribeTransient(response.StatusCode), (int)response.StatusCode);
            }

            return await ReadJsonAsync<T>(response, ct).ConfigureAwait(false);
        }
    }

    /// <summary>带节流与重试的表单 POST + JSON 解析（改名等写操作）。</summary>
    private async Task<T> PostFormWithRetryAsync<T>(
        string url, Func<FormUrlEncodedContent> contentFactory, CancellationToken ct) where T : class
    {
        const int maxAttempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await ThrottleAsync(ct).ConfigureAwait(false);

            using var content = contentFactory();
            using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);

            if (IsTransient(response.StatusCode))
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(RetryBackoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }

                throw new N115ApiException(
                    DescribeTransient(response.StatusCode), (int)response.StatusCode);
            }

            return await ReadJsonAsync<T>(response, ct).ConfigureAwait(false);
        }
    }

    private void AttachCredentialCookies(N115Credential credential)
    {
        foreach (var (name, value) in new[]
                 {
                     ("UID", credential.UID),
                     ("CID", credential.CID),
                     ("SEID", credential.SEID),
                     ("KID", credential.KID),
                 })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                _cookies.Add(new Cookie(name, value, "/", ".115.com"));
            }
            catch
            {
                // Cookie 值非法时忽略，后续接口会以“登录失效”形式暴露
            }
        }
    }

    /// <summary>载入已有凭据（启动时从本地读盘，或换账号时调用）。</summary>
    public void UseCredential(N115Credential credential)
    {
        Credential = credential;
        AttachCredentialCookies(credential);
    }

    // ---------------- 扫码登录 ----------------

    /// <summary>第 1 步：取二维码内容与设备码。</summary>
    public async Task<N115QrSession> StartQrLoginAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiQrToken);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new N115ApiException($"获取二维码失败（HTTP {(int)response.StatusCode}）");
        }

        var parsed = await ReadJsonAsync<N115QrTokenResponse>(response, ct).ConfigureAwait(false);
        if (parsed.State != 1 || parsed.Data is null || string.IsNullOrWhiteSpace(parsed.Data.QrCode))
        {
            throw new N115ApiException($"获取二维码失败：{parsed.Message ?? parsed.Error ?? "接口未返回二维码"}");
        }

        return parsed.Data;
    }

    /// <summary>第 2 步：长轮询二维码状态（服务端无变化时会挂起一段时间再返回）。</summary>
    public async Task<N115QrStatus> PollQrStatusAsync(N115QrSession session, CancellationToken ct)
    {
        var url = $"{ApiQrStatus}?uid={Uri.EscapeDataString(session.Uid ?? string.Empty)}"
                  + $"&time={session.Time}"
                  + $"&sign={Uri.EscapeDataString(session.Sign ?? string.Empty)}"
                  + $"&_={NowMilli()}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var parsed = await ReadJsonAsync<N115QrStatusResponse>(response, ct).ConfigureAwait(false);

        return parsed.Data ?? new N115QrStatus { Status = 0, Msg = "等待扫码" };
    }

    /// <summary>第 3 步：确认后换取登录 Cookie。</summary>
    public async Task<N115Credential> CompleteQrLoginAsync(N115QrSession session, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["account"] = session.Uid ?? string.Empty,
            ["app"] = "web",
        });
        // 115 的登录接口按表单解析参数，Content-Type 必须是 application/x-www-form-urlencoded，
        // 换成 application/json 会被判成「参数错误」。
        using var response = await _http.PostAsync(ApiQrLogin, content, ct).ConfigureAwait(false);
        var parsed = await ReadJsonAsync<N115QrLoginResponse>(response, ct).ConfigureAwait(false);

        if (parsed.State != 1 || parsed.Data?.Cookie is null)
        {
            throw new N115ApiException($"登录失败：{parsed.Message ?? parsed.Error ?? "账号未返回凭据"}");
        }

        var credential = new N115Credential
        {
            UID = parsed.Data.Cookie.UID ?? string.Empty,
            CID = parsed.Data.Cookie.CID ?? string.Empty,
            SEID = parsed.Data.Cookie.SEID ?? string.Empty,
            KID = parsed.Data.Cookie.KID ?? string.Empty,
            UserName = parsed.Data.UserName ?? string.Empty,
            UserId = parsed.Data.UserId,
        };

        if (!credential.IsValid)
        {
            throw new N115ApiException("登录失败：未取得完整 Cookie（UID/CID/SEID）");
        }

        UseCredential(credential);
        return credential;
    }

    // ---------------- 账号校验 ----------------

    /// <summary>
    /// 探测当前 Cookie 是否仍然有效。
    ///
    /// **与旧 CheckCredentialAsync 的关键差别**：探测本身失败（网络波动 / WAF 405 /
    /// 探测端点下线返回 HTML 等）不再折算成「登录失效」—— 2026-10-04 实测
    /// my.115.com 的状态端点下线后，旧实现把每个用户的正常登录都判成失效，
    /// 表现为「每次打开（用户以为是每次升级后）都要重新授权认证」。
    /// 只有接口<b>明确拒绝</b>（990001 / 401 段）才下「确实失效」的结论。
    /// </summary>
    public async Task<N115ProbeOutcome> ProbeCredentialAsync(CancellationToken ct)
    {
        // 第一端点：user/info（轻量）。但它偶发抽风（返回非 JSON / 被拦 / 结构变化），
        // 失败时并不代表凭据失效 —— 2026-10-05 起加第二端点兜底：
        // 登录窗口的完成条件、启动恢复的判定都依赖这里，单端点抖动会让
        // 「重新授权永远等不到验证通过」或「已登录被误杀」。
        var first = await ProbeEndpointAsync(
            $"{ApiCredentialProbe}&_={NowMilli()}", ct).ConfigureAwait(false);
        if (first != N115ProbeOutcome.Inconclusive) return first;

        return await ProbeEndpointAsync(BuildProbeListUrl(), ct).ConfigureAwait(false);
    }

    /// <summary>兜底探测：取 1 条真实目录数据（与「打开目录」同一接口，最稳）。</summary>
    private static string BuildProbeListUrl() =>
        $"{ApiFileList}?aid=1&cid=0&o=user_ptime&asc=0&offset=0&show_dir=1"
        + $"&limit=1&snap=0&natsort=0&record_open_time=1&format=json&fc_mix=0&_={NowMilli()}";

    private async Task<N115ProbeOutcome> ProbeEndpointAsync(string url, CancellationToken ct)
    {
        try
        {
            var parsed = await GetJsonWithRetryAsync<N115BasicResponse>(url, ct).ConfigureAwait(false);
            if (parsed.State) return N115ProbeOutcome.Valid;

            return LooksLikeAuthError(ParseErrno(parsed))
                ? N115ProbeOutcome.AuthExpired
                : N115ProbeOutcome.Inconclusive;
        }
        catch
        {
            return N115ProbeOutcome.Inconclusive;
        }
    }

    /// <summary>取账号昵称（失败返回 null，不影响主流程）。</summary>
    public async Task<N115UserInfoData?> GetUserInfoAsync(CancellationToken ct)
    {
        try
        {
            var parsed = await GetJsonWithRetryAsync<N115UserInfoResponse>(
                $"{ApiUserInfo}&_={NowMilli()}", ct).ConfigureAwait(false);
            return parsed.State ? parsed.Data : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------------- 目录 / 文件 ----------------

    /// <summary>列出目录内容。cid 为 "0" 表示根目录。</summary>
    public async Task<N115FileListResponse> ListAsync(string cid, long offset, long limit, CancellationToken ct)
    {
        if (limit > MaxPageSize) limit = MaxPageSize;
        if (limit <= 0) limit = MaxPageSize;

        var query = "?aid=1"
                    + $"&cid={Uri.EscapeDataString(string.IsNullOrWhiteSpace(cid) ? "0" : cid)}"
                    + "&o=file_name&asc=1"
                    + $"&offset={offset}"
                    + "&show_dir=1"
                    + $"&limit={limit}"
                    + "&snap=0&natsort=0&record_open_time=1&format=json&fc_mix=0";

        var parsed = await GetJsonWithRetryAsync<N115FileListResponse>(
            ApiFileList + query, ct).ConfigureAwait(false);

        ThrowIfFailed(parsed);
        parsed.Data ??= [];
        return parsed;
    }

    /// <summary>重命名单个文件/文件夹。</summary>
    public async Task RenameAsync(string fileId, string newName, CancellationToken ct)
    {
        var parsed = await PostFormWithRetryAsync<N115BasicResponse>(
            ApiFileRename,
            () => new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["fid"] = fileId,
                ["file_name"] = newName,
                [$"files_new_name[{fileId}]"] = newName,
            }),
            ct).ConfigureAwait(false);

        ThrowIfFailed(parsed);
    }

    /// <summary>
    /// 批量移动：把 <paramref name="ids"/> 里的文件/文件夹移动到目录 <paramref name="targetCid"/>。
    ///
    /// 实测（2026-10-03，自建临时文件夹验证）：
    ///   POST /files/move   pid=目标目录  fid[0]=<文件fid 或 文件夹cid>
    /// 文件夹与文件用**同一个 fid[] 字段**，文件夹的位置直接放它的 cid；多条就是 fid[0]、fid[1]…
    /// 一次请求传多条已验证可行，所以不会一条一个请求。
    /// </summary>
    public Task MoveAsync(IReadOnlyList<string> ids, string targetCid, CancellationToken ct)
        => BatchFileOperationAsync(ApiFileMove, ids, new Dictionary<string, string>
        {
            ["pid"] = targetCid,
        }, ct);

    /// <summary>批量复制到目录 <paramref name="targetCid"/>（参数与移动一致，同样支持文件夹）。</summary>
    public Task CopyAsync(IReadOnlyList<string> ids, string targetCid, CancellationToken ct)
        => BatchFileOperationAsync(ApiFileCopy, ids, new Dictionary<string, string>
        {
            ["pid"] = targetCid,
        }, ct);

    /// <summary>
    /// 批量删除（进 115 回收站，可在网页版恢复）。
    /// 实测：POST /rb/delete  pid=所在目录  fid[0]=…  ignore_warn=1。
    ///
    /// 注意 115 的删除是**异步**的：若同一目录上一次删除还没跑完，会返回 errno 990009
    /// 「删除[xxx]操作尚未执行完成，请稍后再试！」。所以这里不做自动重试（重试也一样被拒），
    /// 而是把服务端原文交给界面提示用户稍后再来。
    /// </summary>
    public Task DeleteAsync(IReadOnlyList<string> ids, string parentCid, CancellationToken ct)
        => BatchFileOperationAsync(ApiFileDelete, ids, new Dictionary<string, string>
        {
            ["pid"] = parentCid,
            ["ignore_warn"] = "1",
        }, ct);

    private async Task BatchFileOperationAsync(
        string url, IReadOnlyList<string> ids, Dictionary<string, string> extra, CancellationToken ct)
    {
        if (ids.Count == 0) return;

        var parsed = await PostFormWithRetryAsync<N115BasicResponse>(
            url,
            () =>
            {
                var form = new Dictionary<string, string>(extra);
                for (var i = 0; i < ids.Count; i++)
                {
                    form[$"fid[{i}]"] = ids[i];
                }

                return new FormUrlEncodedContent(form);
            },
            ct).ConfigureAwait(false);

        ThrowIfFailed(parsed);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
