using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using NameTool.Services.N115;

namespace NameTool;

/// <summary>
/// 115 登录窗口。三种方式，按下面的顺序优先：
///   1. 网页登录（默认）——内嵌 WebView2 打开 115 官网，用户登录后自动抓取 Cookie 完成授权；
///   2. 扫码登录——程序自己渲染 115 官方二维码，长轮询扫码状态；
///   3. 手动填 Cookie——兜底，粘贴浏览器里复制出来的 Cookie。
/// 关闭后通过 <see cref="Credential"/> 把凭据交回调用方，null 表示未登录。
/// </summary>
public partial class N115LoginWindow : Window
{
    /// <summary>内嵌浏览器的起始地址：115 首页，右上角有登录入口。</summary>
    private const string HomeUrl = "https://115.com/";

    /// <summary>Cookie 轮询间隔（只读浏览器本地 Cookie，不发网络请求）。</summary>
    private static readonly TimeSpan CookiePollInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>两次「用凭据实探 115 接口」之间的最小间隔。</summary>
    private static readonly TimeSpan ValidateInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 115 官方按 uid 渲染二维码图片，两个 App 通道互为兜底。
    /// 用官方图片可以省掉一个二维码编码库，且内容必然与本次会话一致。
    /// </summary>
    private static readonly string[] QrImageUrls =
    [
        "https://qrcodeapi.115.com/api/1.0/web/1.0/qrcode?uid=",
        "https://qrcodeapi.115.com/api/1.0/mac/1.0/qrcode?uid=",
    ];

    /// <summary>
    /// 注入到内嵌 115 页面里的小脚本，由轮询定时器周期执行，做三件事：
    ///   1. 把 115 页面自己那张登录二维码，换成与本程序「扫码登录」同源的那一张。
    ///      两张图是同一个 uid（同一个会话），但 115 网页版用的那路二维码并不会把手机上的确认
    ///      落到「网页会话」上，所以直接用手机扫页面上的二维码往往扫码成功却没有授权；
    ///      换成 yun.115.com 那路后，页面自己的状态轮询（按 uid/time/sign）依然能收到确认。
    ///   2. 读出页面当前的登录状态（等待扫码 / 已扫码 / 二维码过期），显示到窗口状态栏。
    ///   3. 二维码过期或页面报网络异常时自动点一次刷新（4 秒内最多点一次，避免连点）。
    /// 返回形如 "qr-ok,waiting" 的纯 ASCII 短串。
    /// </summary>
    private const string PageSyncScript = """
        (function () {
            try {
                var out = [];
                var img = document.getElementById('js_login_qrcode_img');
                if (img) {
                    var src = img.getAttribute('src') || '';
                    var m = /[?&]uid=([^&]+)/.exec(src);
                    if (m) {
                        var want = 'https://qrcodeapi.115.com/api/1.0/web/1.0/qrcode?uid=' + m[1];
                        if (src !== want) {
                            var probe = new Image();
                            probe.onload = function () { img.setAttribute('src', want); };
                            probe.src = want;
                            out.push('qr-sync');
                        } else {
                            out.push('qr-ok');
                        }
                    }
                }
                function vis(el) {
                    if (!el) return false;
                    var r = el.getBoundingClientRect();
                    if (r.width <= 0 || r.height <= 0) return false;
                    var s = getComputedStyle(el);
                    return s.display !== 'none' && s.visibility !== 'hidden' && s.opacity !== '0';
                }
                var scene = document.querySelector('.login-scene[lg_rel="qrcode"]');
                if (!vis(scene)) {
                    out.push(document.getElementById('js-login_box') ? 'no-qr' : 'no-box');
                } else {
                    var expired = document.getElementById('js_login_qrcode_refresh');
                    var offline = document.getElementById('js_login_qrcode_offline_refresh');
                    var scanned = document.getElementById('js_login_qrcode_tip_s');
                    if (vis(expired) || vis(offline)) {
                        var now = Date.now();
                        if (!window.__ntQrRefreshAt || now - window.__ntQrRefreshAt > 4000) {
                            window.__ntQrRefreshAt = now;
                            if (vis(expired)) { expired.click(); } else { offline.click(); }
                            out.push('expired');
                        } else {
                            out.push('expired-wait');
                        }
                    } else if (vis(scanned)) {
                        out.push('scanned');
                    } else {
                        out.push('waiting');
                    }
                }
                return out.join(',');
            } catch (e) {
                return 'err';
            }
        })();
        """;

    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly N115Client _client = new();
    private CancellationTokenSource? _cts;
    private bool _finished;

    // ---- 内嵌浏览器状态 ----
    private DispatcherTimer? _cookieTimer;
    private bool _uiReady;
    private bool _webInitStarted;
    private bool _webReady;
    private bool _checkingCookies;
    private bool _pageSyncing;
    private string _lastPageState = string.Empty;
    private DateTime _lastValidateUtc = DateTime.MinValue;

    // ---- 扫码页签按需启动 ----
    private bool _qrStarted;

    public N115LoginWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        _uiReady = true;
    }

    /// <summary>登录成功后的凭据；用户取消时为 null。</summary>
    public N115Credential? Credential { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // 默认页签就是「网页登录」，所以窗口一打开就启动内嵌浏览器
        await InitWebViewAsync();
    }

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

    // ---------------- 页签切换 ----------------

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;

        var onWebTab = ReferenceEquals(Tabs.SelectedItem, WebTab);

        // WebView2 是 HWND 宿主控件，切走后显式收起来，避免盖在其他页签上
        Web.Visibility = onWebTab ? Visibility.Visible : Visibility.Collapsed;

        // 二维码按需生成：只有真的切到扫码页签才发请求
        if (!_qrStarted && ReferenceEquals(Tabs.SelectedItem, QrTab))
        {
            _qrStarted = true;
            _ = StartQrAsync();
        }
    }

    private void GoToQrTab_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = QrTab;

    // ---------------- 内嵌浏览器：网页登录 ----------------

    private async Task InitWebViewAsync()
    {
        if (_webInitStarted) return;
        _webInitStarted = true;

        try
        {
            SetWebStatus("正在启动内嵌浏览器...", "#4F6E95");

            // 独立的用户数据目录：115 的登录状态可以跨会话保留，下次打开免登录。
            // 固定放在 %LOCALAPPDATA%\NameTool\115webview（见 AppDataPaths）——
            // 浏览器会话数据跟着 Windows 用户走，与 exe 装在哪儿、升到什么版本无关，
            // 所以它在任何升级方式下都不会丢。
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

            core.NavigationCompleted += OnWebNavigationCompleted;
            core.Navigate(HomeUrl);

            _webReady = true;
            SetWebStatus("请在内嵌页面里登录 115：用手机 App 扫页面上的二维码，或用页面上的「使用账号登录」", "#4F6E95");

            _cookieTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = CookiePollInterval,
            };
            _cookieTimer.Tick += (_, _) =>
            {
                _ = SyncPageAsync();
                _ = CheckWebCookiesAsync(false);
            };
            _cookieTimer.Start();
        }
        catch (Exception ex)
        {
            _webInitStarted = false;
            _webReady = false;

            NameTool.Services.FileLogSink.Write($"[115] 登录窗口：内嵌浏览器启动失败 - {ex.Message}");
            SetWebStatus("内嵌浏览器启动失败，请改用「扫码登录」页签", "#D62828");
            WebFallbackText.Text = $"内嵌浏览器不可用：{ex.Message}";
            WebFallbackPanel.Visibility = Visibility.Visible;
            RetryWebButton.IsEnabled = false;
            DetectButton.IsEnabled = false;
        }
    }

    private void OnWebNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_webReady || _finished) return;

        // 页面换了，之前记下的状态作废，立刻做一次同步（换二维码 + 读状态）
        _lastPageState = string.Empty;
        _ = SyncPageAsync();
    }

    /// <summary>
    /// 把 115 页面自己的登录状态搬到窗口状态栏，并把页面上的登录二维码换成
    /// 与本程序「扫码登录」同源的那一张（详见 <see cref="PageSyncScript"/>）。
    /// </summary>
    private async Task SyncPageAsync()
    {
        if (!_webReady || _finished || _pageSyncing) return;

        var core = Web.CoreWebView2;
        if (core is null) return;

        _pageSyncing = true;
        try
        {
            var raw = await core.ExecuteScriptAsync(PageSyncScript);
            var state = (raw ?? string.Empty).Trim();
            if (state.Length >= 2 && state[0] == '"' && state[^1] == '"')
            {
                state = state[1..^1];
            }

            if (state.Length == 0 || state == "err" || state == _lastPageState) return;
            _lastPageState = state;

            if (state.Contains("scanned", StringComparison.Ordinal))
            {
                SetWebStatus("页面状态：已扫码，请在手机上点「确认登录」", "#FA8C16");
            }
            else if (state.Contains("expired", StringComparison.Ordinal))
            {
                SetWebStatus("页面状态：二维码已刷新（过期/网络异常会自动刷新），请重新扫码", "#FA8C16");
            }
            else if (state.Contains("waiting", StringComparison.Ordinal))
            {
                SetWebStatus("等待扫码：请用手机 115 App 扫页面上的二维码并确认", "#4F6E95");
            }
            else if (state.Contains("no-qr", StringComparison.Ordinal))
            {
                SetWebStatus("页面里当前没有二维码：请在页面里点「登录」，可选扫码或「使用账号登录」", "#4F6E95");
            }
            else
            {
                SetWebStatus("请在内嵌页面里完成 115 登录", "#4F6E95");
            }
        }
        catch
        {
            // 页面结构变化导致脚本报错时忽略，用户仍可手动登录或改用其他页签
        }
        finally
        {
            _pageSyncing = false;
        }
    }

    private void ReloadWeb_Click(object sender, RoutedEventArgs e)
    {
        if (!_webReady || Web.CoreWebView2 is null)
        {
            _webInitStarted = false;
            WebFallbackPanel.Visibility = Visibility.Collapsed;
            RetryWebButton.IsEnabled = true;
            DetectButton.IsEnabled = true;
            _ = InitWebViewAsync();
            return;
        }

        Web.CoreWebView2.Navigate(HomeUrl);
    }

    private void DetectCookie_Click(object sender, RoutedEventArgs e) => _ = CheckWebCookiesAsync(true);

    private void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!_webReady || Web.CoreWebView2 is null) return;

        if (MessageBox.Show(this,
                "将清除内嵌浏览器里保存的 115 登录状态，然后重新加载页面。\n\n确认继续？",
                "115 网盘", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Web.CoreWebView2.CookieManager.DeleteAllCookies();
            SetWebStatus("已清除内嵌浏览器的登录状态，请重新登录", "#4F6E95");
            Web.CoreWebView2.Navigate(HomeUrl);
        }
        catch (Exception ex)
        {
            SetWebStatus($"清除登录状态失败：{ex.Message}", "#D62828");
        }
    }

    /// <summary>
    /// 从内嵌浏览器读 Cookie（含 HttpOnly，这是它比「手动复制」强的地方），
    /// 集齐 UID/CID/SEID 后再实探一次 115 接口确认可用，才算授权成功。
    /// </summary>
    private async Task CheckWebCookiesAsync(bool userTriggered)
    {
        if (!_webReady || _finished || _checkingCookies) return;

        var core = Web.CoreWebView2;
        if (core is null) return;

        _checkingCookies = true;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(HomeUrl);
            if (cookies is null || cookies.Count == 0)
            {
                if (userTriggered) SetWebStatus("还没有读到任何 Cookie，请先在页面里登录", "#FA8C16");
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
            };

            if (!credential.IsValid)
            {
                if (userTriggered)
                {
                    SetWebStatus("登录凭据还不完整（缺少 UID / CID / SEID），请先在页面里完成登录", "#FA8C16");
                }

                return;
            }

            // 排障：Cookie 齐了但没走到「授权成功」时，日志里要能看到卡在哪一步
            NameTool.Services.FileLogSink.Write(
                $"[115] 登录窗口：已读到完整 Cookie（UID={credential.UID}），正在验证…");

            // 凭据齐了才去实探接口；轮询间隔远小于接口节流间隔，这里限一下频率，
            // 免得登录刚完成那几秒把 my.115.com 打爆
            if (!userTriggered && DateTime.UtcNow - _lastValidateUtc < ValidateInterval) return;
            _lastValidateUtc = DateTime.UtcNow;

            using var probe = new N115Client();
            probe.UseCredential(credential);

            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var outcome = await probe.ProbeCredentialAsync(probeCts.Token);
            if (outcome != N115ProbeOutcome.Valid)
            {
                // 登录刚提交时 Cookie 可能已经写入、服务端尚未生效，下一轮轮询会再试
                NameTool.Services.FileLogSink.Write(
                    $"[115] 登录窗口：Cookie 验证未通过（{outcome}），稍后自动重试");
                if (userTriggered)
                {
                    SetWebStatus(outcome == N115ProbeOutcome.AuthExpired
                        ? "已读到 Cookie，但 115 提示登录已失效，请在页面里重新登录"
                        : "网络异常，暂时无法确认登录状态，稍后自动重试",
                        "#FA8C16");
                }

                return;
            }

            var info = await probe.GetUserInfoAsync(probeCts.Token);
            if (info is not null && !string.IsNullOrWhiteSpace(info.UserName))
            {
                credential.UserName = info.UserName;
                credential.UserId = info.UserId;
            }

            SetWebStatus($"授权成功：{credential.DisplayName}", "#52C41A");
            FinishWith(credential);
        }
        catch (Exception ex)
        {
            if (userTriggered) SetWebStatus($"检测凭据失败：{ex.Message}", "#D62828");
        }
        finally
        {
            _checkingCookies = false;
        }
    }

    // ---------------- 扫码登录 ----------------

    private async Task StartQrAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        QrImage.Source = null;
        RefreshQrButton.IsEnabled = false;
        SetStatus("正在获取二维码...", "#4F6E95");

        try
        {
            var session = await _client.StartQrLoginAsync(cts.Token);

            var image = await LoadQrImageAsync(session.Uid, cts.Token);
            if (image is null)
            {
                SetStatus("二维码获取失败，请改用「网页登录」或「手动填 Cookie」页签", "#D62828");
                return;
            }

            QrImage.Source = image;
            SetStatus("请用 115 手机 App 扫码", "#4F6E95");
            _ = PollQrStatusLoopAsync(session, cts.Token);
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
            if (!_finished) RefreshQrButton.IsEnabled = true;
        }
    }

    private async Task PollQrStatusLoopAsync(N115QrSession session, CancellationToken ct)
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
                // 长轮询超时属于正常现象，继续下一轮
                continue;
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
                    await CompleteQrLoginAsync(session, ct);
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

    private async Task CompleteQrLoginAsync(N115QrSession session, CancellationToken ct)
    {
        try
        {
            var credential = await _client.CompleteQrLoginAsync(session, ct);

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

    // ---------------- 手动填 Cookie ----------------

    private void CookieTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UseCookieButton.IsEnabled = N115Credential.ParseCookieHeader(CookieTextBox.Text) is not null;
    }

    private void PasteCookie_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText()) CookieTextBox.Text = Clipboard.GetText();
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private void UseCookie_Click(object sender, RoutedEventArgs e)
    {
        var credential = N115Credential.ParseCookieHeader(CookieTextBox.Text);
        if (credential is null)
        {
            MessageBox.Show(this,
                "Cookie 解析失败。\n\n需要同时包含 UID、CID、SEID 三项，例如：\nUID=123_ABCDEF...; CID=1A2B...; SEID=...; KID=...",
                "115 网盘登录",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        FinishWith(credential);
    }

    // ---------------- 收尾 ----------------

    private void FinishWith(N115Credential credential)
    {
        if (_finished) return;

        _finished = true;
        Credential = credential;
        _cookieTimer?.Stop();
        _cts?.Cancel();

        // 排障关键（2026-10-05）：登录窗口此前全程无日志，「重新授权无效」无从查起
        NameTool.Services.FileLogSink.Write($"[115] 登录窗口：授权完成（{credential.DisplayName}），准备交给主程序");

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

    /// <summary>
    /// 取 115 官方渲染的登录二维码（内含本次会话 uid），返回 null 表示两个通道都失败。
    /// </summary>
    private static async Task<BitmapImage?> LoadQrImageAsync(string? uid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uid)) return null;

        foreach (var baseUrl in QrImageUrls)
        {
            try
            {
                var bytes = await ImageHttp.GetByteArrayAsync(baseUrl + Uri.EscapeDataString(uid), ct);
                if (bytes.Length > 0) return FromBytes(bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                // 换下一个通道
            }
        }

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

    private void SetWebStatus(string text, string color)
    {
        if (_finished) return;
        WebStatusText.Text = text;
        WebStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void RefreshQr_Click(object sender, RoutedEventArgs e) => _ = StartQrAsync();

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
}
