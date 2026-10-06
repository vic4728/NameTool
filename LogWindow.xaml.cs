using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Documents;
using NameTool.Infrastructure;

namespace NameTool;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();

        // 按级别着色（用户要求：黑底 + 时间戳绿 / 正常白 / 错误失败红）。
        // 条目是纯字符串（带 [MM-dd HH:mm:ss] 时间戳前缀，2026-10-06 起不含年份），用转换器把一行拆成
        // 「时间戳 Run（绿）+ 正文 Run（白/红）」，红 = 行内含 [ERROR] / [FAIL]。
        var itemTemplate = new DataTemplate { DataType = typeof(string) };
        var factory = new FrameworkElementFactory(typeof(ContentControl));
        factory.SetBinding(ContentControl.ContentProperty, new Binding
        {
            Converter = LogEntryInlineConverter.Instance,
        });
        itemTemplate.VisualTree = factory;
        LogList.ItemTemplate = itemTemplate;
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var items = LogList.SelectedItems;
        if (items.Count == 0)
        {
            UiDialog.Show("请先点选（Ctrl / Shift 可多选）要复制的日志行。", "复制日志",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sb = new StringBuilder();
        foreach (var item in items) sb.AppendLine(item.ToString());

        TrySetClipboard(sb.ToString());
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm || vm.Logs.Count == 0)
        {
            UiDialog.Show("日志为空。", "复制日志", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sb = new StringBuilder();
        foreach (var line in vm.Logs) sb.AppendLine(line);

        TrySetClipboard(sb.ToString());
    }

    /// <summary>剪贴板被别的进程占着时 SetText 会抛（COM 异常），提示重试而不是崩溃。</summary>
    private void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            UiDialog.Show("已复制到剪贴板。", "复制日志", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            UiDialog.Show("复制失败（剪贴板被其他程序占用），请再点一次。", "复制日志",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

/// <summary>
/// 日志条目着色转换器：把一行日志拆成「时间戳（绿）+ 正文（白/红）」两段 Run。
/// 级别判定：行内含 <c>[ERROR]</c> / <c>[FAIL]</c>（不区分大小写）→ 正文红；否则正文白。
/// 时间戳格式与 <see cref="ViewModels.MainViewModel"/> 写入时保持一致：[MM-dd HH:mm:ss]（不含年份）。
/// </summary>
public sealed class LogEntryInlineConverter : IValueConverter
{
    public static readonly LogEntryInlineConverter Instance = new();

    private static readonly Brush StampBrush = CreateFrozen(0x39, 0xD3, 0x53);
    private static readonly Brush InfoBrush = CreateFrozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush ErrorBrush = CreateFrozen(0xFF, 0x55, 0x55);

    private static Brush CreateFrozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var line = value as string ?? string.Empty;

        // 时间戳段：[ 开头到第一个 ]（写入格式固定，所以一定存在；防御性处理缺失情况）
        string stamp = string.Empty;
        string rest = line;
        if (line.StartsWith('['))
        {
            var close = line.IndexOf(']');
            if (close > 0)
            {
                stamp = line[..(close + 1)] + ' ';
                rest = line[(close + 1)..].TrimStart();
            }
        }

        var isError = line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)
                      || line.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase);

        // 返回一个现成的 TextBlock（时间戳绿 + 正文白/红），ContentControl 直接承载；
        // 行距 1.35（用户要求「增加行距」，2026-10-06）：行与行之间更透气，长日志更好扫读
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            LineHeight = 15 * 1.35,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        textBlock.Inlines.Add(new Run(stamp) { Foreground = StampBrush });
        textBlock.Inlines.Add(new Run(rest) { Foreground = isError ? ErrorBrush : InfoBrush });
        return textBlock;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
