using System.Text;
using System.Windows;

namespace NameTool;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var items = LogList.SelectedItems;
        if (items.Count == 0)
        {
            MessageBox.Show("请先点选（Ctrl / Shift 可多选）要复制的日志行。", "复制日志",
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
            MessageBox.Show("日志为空。", "复制日志", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show("已复制到剪贴板。", "复制日志", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            MessageBox.Show("复制失败（剪贴板被其他程序占用），请再点一次。", "复制日志",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
