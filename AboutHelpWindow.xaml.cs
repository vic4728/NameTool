using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NameTool.Infrastructure;
using NameTool.Services;

namespace NameTool;

/// <summary>
/// 「关于 / 帮助」弹窗（原「检查更新」按钮位，2026-10-06 起）：
/// GitHub 图标 + 程序信息 + 三个入口（GitHub 地址 / 反馈问题 / 下载新版本），
/// 点按钮用系统默认浏览器打开对应网址。
/// 「下载新版本」下方实时显示线上最新版本号；有新版本时附「查看更新内容」展开按钮。
/// </summary>
public partial class AboutHelpWindow : Window
{
    private List<string>? _latestNotes;

    public AboutHelpWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}";
        // 作者落款固定「作者：Vic4728」（XAML 内凹样式），年份不再拼接

        Loaded += async (_, _) => await LoadLatestVersionAsync();
    }

    /// <summary>查更新服务拿线上最新版本号与更新内容（不弹任何框，结果直接写进界面）。</summary>
    private async Task LoadLatestVersionAsync()
    {
        try
        {
            var service = new UpdateService();
            var result = await service.CheckForUpdateAsync();

            switch (result.Status)
            {
                case UpdateStatus.UpdateAvailable:
                    // result.NewVersion 来自 latestVersion.ToString()（纯数字如 "2.2.0"，无 v 前缀）——
                    // 只在这里加一个 v；GetCurrentVersionText() 自带 v，不能重复加
                    LatestVersionText.Text = $"v{result.NewVersion}（当前 {GetCurrentVersionText()}）";
                    LatestVersionText.Foreground = BrushesFromHex(0x2E, 0x7D, 0x32);   // 绿：有新版
                    LatestHintText.Text = string.IsNullOrWhiteSpace(result.FileSize)
                        ? "检测到新版本，点上方「下载新版本」获取。"
                        : $"检测到新版本（{result.FileSize}），点上方「下载新版本」获取。";
                    _latestNotes = result.Notes;
                    break;

                case UpdateStatus.UpToDate:
                    // 已是最新：同样显示当前版本的更新内容（用户想看「最新版更新了什么」不需要有新版才给看）
                    LatestVersionText.Text = $"{GetCurrentVersionText()}（已是最新）";
                    LatestVersionText.Foreground = BrushesFromHex(0x2E, 0x7D, 0x32);
                    LatestHintText.Text = "当前已是最新版本。";
                    _latestNotes = result.Notes;
                    break;

                default:
                    LatestVersionText.Text = "获取失败";
                    LatestVersionText.Foreground = BrushesFromHex(0xD6, 0x28, 0x28);   // 红：失败
                    LatestHintText.Text = "无法连接更新服务器，可稍后重试或直接点上方「下载新版本」。";
                    break;
            }
        }
        catch
        {
            LatestVersionText.Text = "获取失败";
            LatestVersionText.Foreground = BrushesFromHex(0xD6, 0x28, 0x28);
            LatestHintText.Text = "无法连接更新服务器，可稍后重试或直接点上方「下载新版本」。";
        }

        // 有更新说明才显示「更新内容」按钮（放在最后统一处理，避免分支漏设）
        ToggleNotesButton.Visibility = _latestNotes is { Count: > 0 }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private string GetCurrentVersionText()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "?" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>「更新内容」按钮：打开独立弹窗显示最新版本更新说明（2026-10-06 起不再内嵌展开）。</summary>
    private void ToggleNotes_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ReleaseNotesWindow { Owner = this };
        dlg.ShowDialog();
    }

    private static System.Windows.Media.SolidColorBrush BrushesFromHex(byte r, byte g, byte b)
        => new(System.Windows.Media.Color.FromRgb(r, g, b));

    /// <summary>用系统默认浏览器打开网址（UseShellExecute=true 走系统关联，不依赖内置浏览器）。</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UiDialog.Show($"打开浏览器失败：{ex.Message}\n\n请手动访问：{url}", "关于 / 帮助",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/vic4728/NameTool");

    private void OpenIssues_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/vic4728/NameTool/issues");

    private void OpenDownload_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://svr.jcsit.cn/NameTool/update/");
}
