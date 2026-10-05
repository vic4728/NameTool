using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NameTool;

/// <summary>
/// 「批量序号 · 变量说明」弹窗。
/// <para>
/// 每个变量一行：变量名用**无边框只读 TextBox**（可拖选 / Ctrl+A / Ctrl+C，右键自带复制菜单，
/// 与网盘重命名弹窗「原文件名」同一做法），右侧再给一个「复制」按钮一键进剪贴板 ——
/// 用户的需求原话是「弹窗内变量可进行复制」，两条路都给。
/// 「查找替换 · 变量」一节已挪到批量替换区的「说明」弹窗（ReplaceVariablesWindow）。
/// </para>
/// </summary>
public partial class SequenceVariablesWindow : Window
{
    /// <summary>变量名与给用户看的一句话说明（顺序即展示顺序）。</summary>
    private static readonly (string Variable, string Description)[] Variables =
    [
        ("{name}", "原名称（不含扩展名）。例：视频 → 「视频.mp4」的原名称是「视频」"),
        ("{n}", "序号（数字 / 字母由「字母编码」「字母大写」开关决定）"),
        ("{a}", "英文小写序号：a, b, c …（不受开关影响）"),
        ("{A}", "英文大写序号：A, B, C …（不受开关影响）"),
        ("{date}", "修改日期（年-月-日，如 2026-10-04）"),
        ("{ext}", "文件类型（扩展名，不含点。例：mp4、srt）"),
        ("#", "等价于 {n} 的旧写法（一个或多个 # 都可以），继续支持"),
    ];

    public SequenceVariablesWindow()
    {
        InitializeComponent();

        foreach (var (variable, description) in Variables)
        {
            RowsHost.Children.Add(BuildRow(variable, description));
        }
    }

    private static UIElement BuildRow(string variable, string description)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 只读无边框 TextBox：文本能被选中、Ctrl+C、右键复制（TextBlock 做不到）
        var box = new TextBox
        {
            Text = variable,
            IsReadOnly = true,
            IsTabStop = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x90, 0xFF)),
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(2, 3, 2, 3),
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0xCD, 0xF0)),
        };
        Grid.SetColumn(box, 0);
        row.Children.Add(box);

        var desc = new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x6B, 0x7D)),
        };
        Grid.SetColumn(desc, 1);
        row.Children.Add(desc);

        var copy = new Button { Content = "复制", Tag = variable };
        copy.Click += (_, _) => CopyToClipboard(variable);
        Grid.SetColumn(copy, 3);
        row.Children.Add(copy);

        return row;
    }

    private static void CopyToClipboard(string variable)
    {
        try
        {
            Clipboard.SetText(variable);
        }
        catch
        {
            // 剪贴板被别的进程占着时 SetText 会抛（COM 异常），点一次没复制上再点一次就行
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
