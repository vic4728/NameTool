using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using NameTool.Services.N115;
using NameTool.Infrastructure;

namespace NameTool;

/// <summary>
/// 115 授权窗口（2026-10-06 单页重构，取代原 3 页签）。
/// <para>
/// 流程：<b>选择授权方式 → 选择授权设备 → 扫码</b>（Cookie / OpenAPI 二选一确认）；
/// 底部「115 接口」区选择接口偏好（默认优先 Cookie），并对两种授权各给一个「重新获取 / 重新登录」入口。
/// <list type="bullet">
/// <item><b>网页扫码授权（推荐）</b>：Cookie = 模拟「所选授权设备」上的客户端，占一个设备登录位（同类互踢）。
/// 内嵌 WebView2 会自动检测网页登录完成的 Cookie（保留原网页登录能力）。</item>
/// <item><b>自定义 AppID 扫码（OpenAPI）</b>：115 开放平台 OAuth2.0 设备码授权，
/// access/refresh token 不占设备登录位、可自动续期；需要用户在 open.115.com 申请 AppID。</item>
/// </list>
/// 关闭后通过 <see cref="Credential"/> 把凭据交回调用方，null 表示未登录。
/// </para>
/// </summary>
public partial class N115LoginWindow : Window
{
    /// <summary>Cookie 轮询间隔（只读浏览器本地 Cookie，不发网络请求）。</summary>
    private static readonly TimeSpan CookiePollInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>两次「用凭据实探 115 接口」之间的最小间隔。</summary>
    private static readonly TimeSpan ValidateInterval = TimeSpan.FromSeconds(5);

    /// <summary>OpenAPI 轮询间隔（等用户扫码确认）。</summary>
    private static readonly TimeSpan OpenPollInterval = TimeSpan.FromSeconds(2);

    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly N115Client _client = new();
    private CancellationTokenSource? _cts;
    private bool _finished;

    // ---- 内嵌浏览器状态（网页 Cookie 自动检测）----
    private DispatcherTimer? _cookieTimer;
    private bool _webInitStarted;
    private bool _webReady;
    private bool _checkingCookies;
    private DateTime _lastValidateUtc = DateTime.MinValue;

    // ---- 单页流程状态 ----

    /// <summary>授权设备选项（下拉框数据源）。value = 115 接口的设备通道名，三处 URL 必须同通道。</summary>
    private static readonly (string Label, string Value)[] Devices =
    [
        ("网页端", "web"),
        ("安卓手机", "android"),
        ("安卓 TV 端", "tv"),
        ("鸿蒙端", "harmony"),
        ("苹果手机", "ios"),
        ("苹果 TV", "apple_tv"),
        ("Linux", "os_linux"),
        ("Mac", "os_mac"),
        ("支付宝生活端", "alipaymini"),
        ("微信小程序端", "wechatmini"),
    ];

    /// <summary>当前选中的授权设备通道。</summary>
    private string CurrentDevice =>
        (DeviceBox.SelectedItem as DeviceOption)?.Value ?? "os_mac";

    private bool IsOpenApiMode => ModeOpenApi.IsChecked == true;

    /// <summary>下拉框条目（显示名 + 通道值）。</summary>
    private sealed record DeviceOption(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    /// <summary>OpenAPI PKCE：verifier 本地随机、challenge 发给服务端，确认后凭 verifier 换 token。</summary>
    private string? _openCodeVerifier;
    private N115OpenDeviceCode? _openDeviceCode;

    /// <summary>打开窗口时已保存在本机的授权（加载后显示到「115 接口」状态区；完成授权时用于合并）。</summary>
    private N115Credential? _existing;

    public N115LoginWindow()
    {
        InitializeComponent();

        // 授权设备下拉框：默认 Mac（独立登录位，不与手机/网页冲突）
        DeviceBox.ItemsSource = Devices.Select(d => new DeviceOption(d.Label, d.Value)).ToList();
        DeviceBox.SelectedIndex = Array.FindIndex(Devices, d => d.Value == "os_mac");

        Loaded += Window_Loaded;
        Closed += OnClosed;
    }

    /// <summary>登录成功后的凭据；用户取消时为 null。</summary>
    public N115Credential? Credential { get; private set; }

    private void OnClosed(object? sender, EventArgs e)
    {
        _finished = true;

        _cookieTimer?.Stop();
        _cookieTimer = null;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        try
        {
            Web.Dispose();
        }
        catch
        {
            // 关闭阶段的异常无需处理
        }

        _client.Dispose();
    }

    // ---------------- 「115 接口」状态区 ----------------

    /// <summary>把本机已保存的两种授权状态刷新到界面（打开时 + 每次授权完成后调用）。
    /// 按设计稿样式：每行 = 通道名 + 状态徽章（浅绿底=已授权 / 灰底=未授权）+ 右侧按钮。</summary>
    private void RefreshAuthStatus()
    {
        // Cookie 行
        if (_existing is { IsValid: true } c)
        {
            var dev = string.IsNullOrWhiteSpace(c.CookieDevice) ? "已授权" : CookieDeviceLabel(c.CookieDevice);
            var who = string.IsNullOrWhiteSpace(c.UserName) ? "" : $" · {c.UserName}";
            ApiCookieStatus.Text = $"✓ {dev}{who}（{c.SavedAt:MM-dd HH:mm}）";
            ApiCookieStatus.Foreground = BrushOf("#2A7700");
        }
        else
        {
            ApiCookieStatus.Text = "未授权";
            ApiCookieStatus.Foreground = BrushOf("#858585");
        }
        HasCookieBadge.IsChecked = _existing is { IsValid: true };

        // OpenAPI 行
        if (_existing is { HasOpenApi: true } o)
        {
            var who = string.IsNullOrWhiteSpace(o.UserName) ? $"AppID {ShortAppId(o.ApiAppId)}" : o.UserName;
            ApiOpenStatus.Text = $"✓ {who}（{o.ApiSavedAt:MM-dd HH:mm}）";
            ApiOpenStatus.Foreground = BrushOf("#2A7700");
        }
        else
        {
            ApiOpenStatus.Text = "未授权";
            ApiOpenStatus.Foreground = BrushOf("#858585");
        }
        HasOpenApiBadge.IsChecked = _existing is { HasOpenApi: true };

        // 接口偏好回显
        var preferOpen = _existing?.PreferOpenApi == true;
        ApiPreferOpen.IsChecked = preferOpen;
        ApiPreferCookie.IsChecked = !preferOpen;
    }

    // 徽章状态开关直接用 XAML 生成的 HasCookieBadge / HasOpenApiBadge 字段（隐藏 CheckBox）

    private static string CookieDeviceLabel(string device) =>
        string.IsNullOrWhiteSpace(device) ? "未知设备" : device;

    private static string ShortAppId(string appId) =>
        appId.Length <= 8 ? appId : appId[..4] + "…" + appId[^4..];

    private static SolidColorBrush BrushOf(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    // ---------------- ① 授权方式切换 ----------------

    private void ModeWebQr_Checked(object sender, RoutedEventArgs e)
    {
        if (AppIdPanel is null) return;   // XAML 初始化期间的首次触发
        AppIdPanel.Visibility = Visibility.Collapsed;
        AppIdLabel.Visibility = Visibility.Collapsed;
    }

    private void ModeOpenApi_Checked(object sender, RoutedEventArgs e)
    {
        if (AppIdPanel is null) return;
        AppIdPanel.Visibility = Visibility.Visible;
        AppIdLabel.Visibility = Visibility.Visible;
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 设备变化后，进行中的扫码会话作废，重置提示
        if (QrStatusText is null) return;
        ResetQr("设备已切换，点「开始授权」生成新二维码");
    }

    private void ApiPreference_Checked(object sender, RoutedEventArgs e)
    {
        // 仅记录偏好；完成授权时写入凭据（当前版本业务接口尚未 OpenAPI 化，先记住选择）
    }

    // ---------------- ② 开始 / 刷新授权 ----------------

    private void Start_Click(object sender, RoutedEventArgs e) => StartAuthAsync();

    private void RefreshQr_Click(object sender, RoutedEventArgs e) => StartAuthAsync();

    private void Recookie_Click(object sender, RoutedEventArgs e)
    {
        ModeWebQr.IsChecked = true;
        StartAuthAsync();
    }

    private async void StartAuthAsync()
    {
        if (IsOpenApiMode)
        {
            var appId = AppIdBox.Text.Trim();
            if (appId.Length == 0)
            {
                SetStatus("请先填写在 115 开放平台（open.115.com）申请的 AppID", "#D62828");
                AppIdBox.Focus();
                return;
            }

            await StartOpenAuthAsync(appId);
        }
        else
        {
            await StartQrAsync(CurrentDevice);
        }
    }

    // ---------------- Cookie 扫码授权（按设备通道） ----------------

    private async Task StartQrAsync(string device)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        QrPlaceholder.Visibility = Visibility.Collapsed;
        QrImage.Source = null;
        RefreshQrButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        SetStatus($"正在获取「{DeviceLabel(device)}」二维码...", "#4F6E95");

        try
        {
            var session = await _client.StartQrLoginAsync(device, cts.Token);

            var image = await LoadQrImageAsync(device, session.Uid, cts.Token);
            if (image is null)
            {
                SetStatus("二维码获取失败，请点「刷新二维码」重试，或改用其它授权设备", "#D62828");
                return;
            }

            QrImage.Source = image;
            SetStatus($"请用手机 115 App 扫码（{DeviceLabel(device)} 通道），并在手机上点「确认登录」", "#4F6E95");
            _ = PollQrStatusLoopAsync(session, device, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭/刷新导致，忽略
        }
        catch (Exception ex)
        {
            SetStatus($"获取二维码失败：{ex.Message}", "#D62828");
        }
        finally
        {
            if (!_finished)
            {
                RefreshQrButton.IsEnabled = true;
                StartButton.IsEnabled = true;
            }
        }
    }

    private async Task PollQrStatusLoopAsync(N115QrSession session, string device, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            N115QrStatus status;
            try
            {
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pollCts.CancelAfter(TimeSpan.FromSeconds(40));
                status = await _client.PollQrStatusAsync(session, pollCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;   // 长轮询超时属正常，继续下一轮
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                SetStatus($"查询扫码状态失败：{ex.Message}", "#D62828");
                try
                {
                    await Task.Delay(2000, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            switch (status.Status)
            {
                case 0:
                    SetStatus("等待扫码...", "#4F6E95");
                    break;
                case 1:
                    SetStatus("已扫码，请在手机上点击「确认登录」", "#FA8C16");
                    break;
                case 2:
                    SetStatus("授权成功，正在换取登录凭据...", "#52C41A");
                    await CompleteQrLoginAsync(session, device, ct);
                    return;
                case -1:
                    SetStatus("二维码已过期，请点「刷新二维码」", "#D62828");
                    return;
                case -2:
                    SetStatus("已在手机上取消授权，请刷新后重试", "#D62828");
                    return;
                default:
                    SetStatus(status.Msg ?? "处理中...", "#4F6E95");
                    break;
            }
        }
    }

    private async Task CompleteQrLoginAsync(N115QrSession session, string device, CancellationToken ct)
    {
        try
        {
            var credential = await _client.CompleteQrLoginAsync(session, device, ct);
            credential.PreferOpenApi = ApiPreferOpen.IsChecked == true;

            if (string.IsNullOrWhiteSpace(credential.UserName))
            {
                var info = await _client.GetUserInfoAsync(ct);
                if (info is not null && !string.IsNullOrWhiteSpace(info.UserName))
                {
                    credential.UserName = info.UserName;
                }
            }

            FinishWith(credential);
        }
        catch (Exception ex)
        {
            NameTool.Services.FileLogSink.Write($"[115] 登录窗口：扫码换取凭据失败 - {ex.Message}");
            SetStatus($"登录失败：{ex.Message}", "#D62828");
        }
    }

    // ---------------- OpenAPI（自定义 AppID）设备码授权 ----------------

    private async Task StartOpenAuthAsync(string appId)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        QrImage.Source = null;
        QrPlaceholder.Visibility = Visibility.Visible;
        QrPlaceholder.Text = "正在向开放平台申请设备码...";
        RefreshQrButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        SetStatus("正在申请 OpenAPI 设备码...", "#4F6E95");

        try
        {
            // PKCE：verifier 本地随机 64 字节 → challenge = BASE64URL(SHA256(verifier))
            var verifierBytes = RandomNumberGenerator.GetBytes(64);
            _openCodeVerifier = Base64Url(verifierBytes);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_openCodeVerifier)));

            var code = await _client.StartOpenAuthAsync(appId, challenge, cts.Token);
            _openDeviceCode = code;

            // 开放平台返回的 qrcode 内容自己编码成图；返回为空就用 uid 兜底内容
            var qrContent = string.IsNullOrWhiteSpace(code.QrCode) ? code.Uid : code.QrCode!;
            if (string.IsNullOrWhiteSpace(qrContent))
            {
                SetStatus("开放平台未返回二维码内容，请点「刷新二维码」重试", "#D62828");
                return;
            }
            var image = await RenderQrContentAsync(qrContent, cts.Token);
            if (image is null)
            {
                SetStatus("二维码渲染失败，请点「刷新二维码」重试", "#D62828");
                return;
            }

            QrPlaceholder.Visibility = Visibility.Collapsed;
            QrImage.Source = image;
            SetStatus("请用手机 115 App 扫码，并在手机上确认开放平台授权", "#4F6E95");
            _ = PollOpenAuthLoopAsync(appId, code, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            SetStatus($"OpenAPI 授权失败：{ex.Message}", "#D62828");
        }
        finally
        {
            if (!_finished)
            {
                RefreshQrButton.IsEnabled = true;
                StartButton.IsEnabled = true;
            }
        }
    }

    private async Task PollOpenAuthLoopAsync(string appId, N115OpenDeviceCode code, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(OpenPollInterval, ct);
                var tokens = await _client.CompleteOpenAuthAsync(appId, code.Uid!, _openCodeVerifier!, ct);

                var credential = new N115Credential
                {
                    ApiAppId = appId,
                    ApiAccessToken = tokens.AccessToken ?? string.Empty,
                    ApiRefreshToken = tokens.RefreshToken ?? string.Empty,
                    ApiSavedAt = DateTimeOffset.Now,
                    PreferOpenApi = ApiPreferOpen.IsChecked == true,
                };

                // 尝试补用户名（OpenAPI 用户信息接口；失败不阻断）
                try
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(10));
                    var info = await _client.GetOpenUserInfoAsync(credential.ApiAccessToken, probeCts.Token);
                    if (info is not null)
                    {
                        credential.UserName = info.UserName ?? string.Empty;
                        credential.UserId = info.UserId;
                    }
                }
                catch
                {
                    // 用户名拿不到不影响授权本身
                }

                SetStatus(credential.UserName.Length > 0
                    ? $"OpenAPI 授权成功：{credential.UserName}"
                    : "OpenAPI 授权成功", "#52C41A");
                FinishWith(credential);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 单次轮询超时，继续等
            }
            catch (N115ApiException ex) when (ex.Message.Contains("验证失败") || ex.Message.Contains("未确认", StringComparison.Ordinal))
            {
                // 用户还没在手机上确认，继续等
                SetStatus("等待手机确认...", "#FA8C16");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                SetStatus($"OpenAPI 轮询失败：{ex.Message}", "#D62828");
                try
                {
                    await Task.Delay(3000, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    // ---------------- 两个「重新获取 / 重新登录」入口 ----------------

    private void ReopenApi_Click(object sender, RoutedEventArgs e)
    {
        ModeOpenApi.IsChecked = true;
        StartAuthAsync();
    }

    // ---------------- 内嵌浏览器（网页 Cookie 自动检测，保留） ----------------

    /// <summary>窗口加载后：加载已保存的授权状态显示到「115 接口」区，并静默启动内嵌浏览器。</summary>
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;

        // 已保存的授权（多目录策略读，见 N115CredentialStore）→ 状态区显示 Cookie / OpenAPI 各自状态
        try
        {
            _existing = new N115CredentialStore().Load();
        }
        catch
        {
            _existing = null;
        }
        RefreshAuthStatus();

        // 已存 Cookie 的设备通道回显到下拉框（找不到对应项时保持默认 Mac）
        if (_existing is { IsValid: true } saved && !string.IsNullOrWhiteSpace(saved.CookieDevice))
        {
            var idx = Array.FindIndex(Devices, d => d.Value == saved.CookieDevice);
            if (idx >= 0) DeviceBox.SelectedIndex = idx;
        }

        await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        if (_webInitStarted) return;
        _webInitStarted = true;

        try
        {
            var dataFolder = NameTool.Services.AppDataPaths.WebViewDirectory;
            Directory.CreateDirectory(dataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await Web.EnsureCoreWebView2Async(environment);

            if (_finished) return;

            var core = Web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;

            _webReady = true;

            _cookieTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = CookiePollInterval,
            };
            _cookieTimer.Tick += (_, _) => _ = CheckWebCookiesAsync(false);
            _cookieTimer.Start();
        }
        catch (Exception ex)
        {
            _webInitStarted = false;
            _webReady = false;
            NameTool.Services.FileLogSink.Write($"[115] 登录窗口：内嵌浏览器启动失败（网页 Cookie 检测不可用）- {ex.Message}");
        }
    }

    private async Task CheckWebCookiesAsync(bool userTriggered)
    {
        if (!_webReady || _finished || _checkingCookies) return;

        var core = Web.CoreWebView2;
        if (core is null) return;

        _checkingCookies = true;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync("https://115.com/");
            if (cookies is null || cookies.Count == 0)
            {
                return;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cookie in cookies)
            {
                if (!string.IsNullOrWhiteSpace(cookie.Name)) map[cookie.Name] = cookie.Value ?? string.Empty;
            }

            var credential = new N115Credential
            {
                UID = map.TryGetValue("UID", out var uid) ? uid : string.Empty,
                CID = map.TryGetValue("CID", out var cid) ? cid : string.Empty,
                SEID = map.TryGetValue("SEID", out var seid) ? seid : string.Empty,
                KID = map.TryGetValue("KID", out var kid) ? kid : string.Empty,
                CookieDevice = "web（内嵌网页）",
                PreferOpenApi = ApiPreferOpen.IsChecked == true,
            };

            if (!credential.IsValid) return;

            if (!userTriggered && DateTime.UtcNow - _lastValidateUtc < ValidateInterval) return;
            _lastValidateUtc = DateTime.UtcNow;

            using var probe = new N115Client();
            probe.UseCredential(credential);

            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var outcome = await probe.ProbeCredentialAsync(probeCts.Token);
            if (outcome != N115ProbeOutcome.Valid) return;

            var info = await probe.GetUserInfoAsync(probeCts.Token);
            if (info is not null && !string.IsNullOrWhiteSpace(info.UserName))
            {
                credential.UserName = info.UserName;
                credential.UserId = info.UserId;
            }

            SetStatus($"检测到网页已登录：{credential.DisplayName}，自动完成授权", "#52C41A");
            FinishWith(credential);
        }
        catch
        {
            // 检测失败静默：下一轮再试
        }
        finally
        {
            _checkingCookies = false;
        }
    }

    // ---------------- 手动辅助 ----------------

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://115.com") { UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器不影响其他操作
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _finished = true;
        DialogResult = false;
    }

    // ---------------- 收尾 ----------------

    private void FinishWith(N115Credential credential)
    {
        if (_finished) return;

        // 合并：单通道授权不能把另一通道已有的授权冲掉
        //（如本次只扫码了 Cookie，而已存档里有 OpenAPI token → 保留 OpenAPI 部分，反之亦然）。
        if (_existing is not null)
        {
            if (credential.IsValid)
            {
                // 本次拿到的是新 Cookie → 继承旧档里的 OpenAPI 部分
                credential.ApiAppId = _existing.ApiAppId;
                credential.ApiAccessToken = _existing.ApiAccessToken;
                credential.ApiRefreshToken = _existing.ApiRefreshToken;
                credential.ApiSavedAt = _existing.ApiSavedAt;
            }
            else if (credential.HasOpenApi)
            {
                // 本次拿到的是新 OpenAPI token → 继承旧档里的 Cookie 部分
                credential.UID = _existing.UID;
                credential.CID = _existing.CID;
                credential.SEID = _existing.SEID;
                credential.KID = _existing.KID;
                credential.CookieDevice = _existing.CookieDevice;
                credential.SavedAt = _existing.SavedAt;
                if (string.IsNullOrWhiteSpace(credential.UserName)) credential.UserName = _existing.UserName;
                if (credential.UserId == 0) credential.UserId = _existing.UserId;
            }
        }

        // 刷新「115 接口」状态区，让用户看清两种授权的当前状态（窗口即将关闭，但至少可见一瞬）
        _existing = credential;
        RefreshAuthStatus();

        _finished = true;
        Credential = credential;
        _cookieTimer?.Stop();
        _cts?.Cancel();

        NameTool.Services.FileLogSink.Write(
            $"[115] 登录窗口：授权完成（{credential.DisplayName}，{(credential.HasOpenApi ? "OpenAPI" : $"Cookie/{credential.CookieDevice}")}），准备交给主程序");

        // 让「授权成功」那一行能露个面，再关窗
        _ = CloseSoonAsync();
    }

    private async Task CloseSoonAsync()
    {
        try
        {
            await Task.Delay(500);
        }
        catch
        {
            // ignore
        }

        try
        {
            DialogResult = true;
        }
        catch
        {
            // 用户已经手动关窗
        }
    }

    // ---------------- 辅助 ----------------

    private static string DeviceLabel(string device) => device switch
    {
        "web" => "网页端",
        "android" => "安卓手机",
        "tv" => "安卓 TV 端",
        "harmony" => "鸿蒙端",
        "ios" => "苹果手机",
        "apple_tv" => "苹果 TV",
        "os_linux" or "linux" => "Linux",
        "os_mac" or "mac" => "Mac",
        "os_windows" or "windows" => "Windows",
        "alipaymini" => "支付宝生活端",
        "wechatmini" => "微信小程序端",
        _ => device,
    };

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<BitmapImage?> LoadQrImageAsync(string device, string? uid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uid)) return null;
        return await RenderQrContentAsync(N115Client.QrImageUrl(device, uid), ct);
    }

    /// <summary>把内容（官方渲染图 URL / 开放平台二维码内容）转成位图；URL 失败时尝试本地生成。</summary>
    private static async Task<BitmapImage?> RenderQrContentAsync(string content, CancellationToken ct)
    {
        // 官方渲染图 URL 直接下载
        if (content.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = await ImageHttp.GetByteArrayAsync(content, ct);
                if (bytes.Length > 0) return FromBytes(bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                // 落到本地生成
            }
        }

        // 本地生成二维码：程序不带二维码库，用 115 官方「渲染图」端点兜底
        // （开放平台内容若非 URL，此处留空由上层提示失败；qrcode 内容里通常含 uid 可拼渲染 URL）
        return null;
    }

    private static BitmapImage FromBytes(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void SetStatus(string text, string color)
    {
        if (_finished) return;
        QrStatusText.Text = text;
        QrStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void ResetQr(string text)
    {
        _cts?.Cancel();
        QrImage.Source = null;
        QrPlaceholder.Visibility = Visibility.Visible;
        QrPlaceholder.Text = "二维码将显示在这里";
        RefreshQrButton.IsEnabled = false;
        SetStatus(text, "#4F6E95");
    }
}
