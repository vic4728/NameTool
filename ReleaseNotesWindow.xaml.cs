using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NameTool.Infrastructure;
using NameTool.Services;

namespace NameTool;

/// <summary>
/// 「更新内容」弹窗（2026-10-06 起，从「关于/帮助」的「更新内容」按钮打开）：
/// 头部蓝紫渐变横幅（版本徽章），正文按「✨ 更新功能 / 🔧 修复问题 / 📋 其它说明」
/// 渲染成三组彩色左边条圆角卡片——精致风、多颜色搭配（2026-10-06 用户要求）。
/// 文案二次精简：每条只留第一句核心短句（超 30 字截断），一行一条干净利落。
/// </summary>
public partial class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow()
    {
        InitializeComponent();

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var result = await new UpdateService().CheckForUpdateAsync();

            switch (result.Status)
            {
                case UpdateStatus.UpdateAvailable:
                    // result.NewVersion 是纯数字（无 v），只在这里加一个 v
                    LatestVersionText.Text = $"v{result.NewVersion}";
                    HintText.Text = string.IsNullOrWhiteSpace(result.FileSize)
                        ? "检测到新版本，可到更新服务器下载。"
                        : $"检测到新版本（{result.FileSize}），可到更新服务器下载。";
                    FillNotes(result.Notes);
                    break;

                case UpdateStatus.UpToDate:
                    // 已是最新：显示当前版本的更新内容（UpToDate 分支也带当前版 Notes）
                    LatestVersionText.Text = GetVersionText();
                    HintText.Text = "当前已是最新版本，以下为该版本更新内容。";
                    FillNotes(result.Notes);
                    break;

                default:
                    LatestVersionText.Text = "—";
                    HintText.Text = "无法连接更新服务器，请稍后重试。";
                    EmptyText.Text = result.Message;
                    break;
            }
        }
        catch (Exception ex)
        {
            LatestVersionText.Text = "—";
            HintText.Text = "无法连接更新服务器，请稍后重试。";
            EmptyText.Text = ex.Message;
        }
    }

    /// <summary>把更新说明精简后按「更新功能 / 修复问题 / 其它说明」分组渲染成彩色卡片。</summary>
    private void FillNotes(List<string>? notes)
    {
        if (notes is not { Count: > 0 })
        {
            EmptyText.Text = "服务器未提供本版本的更新说明。";
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;

        // 文案二次精简：只留第一句（顿号/句号/括号前的核心短句），再压缩空白与冗余前缀
        var slimmed = notes
            .Select(SlimLine)
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();

        // 关键词分组：修复 / 优化改进 / 其它兜底；其余全部进「更新功能」
        var features = new List<string>();
        var fixes = new List<string>();
        var others = new List<string>();

        foreach (var note in slimmed)
        {
            if (note.Contains("修复", StringComparison.Ordinal))
            {
                fixes.Add(note);
            }
            else if (note.Contains("优化", StringComparison.Ordinal)
                     || note.Contains("调整", StringComparison.Ordinal)
                     || note.Contains("移除", StringComparison.Ordinal))
            {
                others.Add(note);
            }
            else
            {
                features.Add(note);
            }
        }

        AddGroup("✨", "更新功能", "#1890FF", "#E6F4FF", features);
        AddGroup("🔧", "修复问题", "#52C41A", "#F0FBEB", fixes);
        AddGroup("📋", "其它说明", "#8A94A6", "#F5F6F8", others);
    }

    /// <summary>
    /// <summary>
    /// 单条精简：取第一句（到第一个「：」「，」「；」「。」「（」为止，上限 30 字符再截断加省略号），
    /// 去掉常见冗余开头，保证一行一条短句、干净利落。
    /// </summary>
    private static string SlimLine(string raw)
    {
        var s = raw.Trim();

        // 取第一句（分隔符之前的部分）
        foreach (var sep in new[] { "：", "，", "；", "。", "（" })
        {
            var idx = s.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 4)
            {
                s = s[..idx];
                break;
            }
        }

        // 去掉常见冗余开头
        foreach (var prefix in new[] { "新增「", "支持", "现在", "修复「" })
        {
            if (s.StartsWith(prefix, StringComparison.Ordinal) && s.Length > prefix.Length + 2)
            {
                s = s[prefix.Length..];
                break;
            }
        }

        s = s.TrimEnd('：', '，', '；', '。', '」', '、');

        // 上限 30 字符，超出截断（保证一行放得下，不换行）
        if (s.Length > 30)
        {
            s = s[..30].TrimEnd() + "…";
        }

        return s;
    }

    /// 加一组彩色卡片：左侧 4px 竖色条 + 彩色图标徽章 + 组名 + 圆点条目。
    /// </summary>
    private void AddGroup(string icon, string title, string accentHex, string tintHex, List<string> items)
    {
        if (items.Count == 0) return;

        var accent = (SolidColorBrush)new BrushConverter().ConvertFromString(accentHex)!;

        // 组卡片：白底圆角 + 左侧 4px 竖色条（Grid 两列：条 + 内容）
        var cardPanel = new StackPanel();

        // 组头：彩色圆角小徽章（图标）+ 组名
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        header.Children.Add(new Border
        {
            Background = (SolidColorBrush)new BrushConverter().ConvertFromString(tintHex)!,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = icon,
                FontSize = 12,
                Foreground = accent,
            },
        });
        header.Children.Add(new TextBlock
        {
            Text = " " + title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2C, 0x3E, 0x50)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        cardPanel.Children.Add(header);

        // 条目：accent 色圆点 + 深灰正文
        foreach (var item in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 3) };
            row.Children.Add(new TextBlock
            {
                Text = "•",
                FontSize = 12,
                Foreground = accent,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
            });
            row.Children.Add(new TextBlock
            {
                Text = item,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x5E)),
                LineHeight = 18,
            });
            cardPanel.Children.Add(row);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(2),
            Width = 4,
            Margin = new Thickness(0, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(cardPanel, 1);
        grid.Children.Add(bar);
        grid.Children.Add(cardPanel);

        NotesHost.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xEC, 0xF6)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = grid,
        });
    }

    private static string GetVersionText()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "?" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
