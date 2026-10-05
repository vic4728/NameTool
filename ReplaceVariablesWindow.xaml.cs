using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NameTool;

/// <summary>
/// 「批量替换 · 说明」弹窗（样式与「批量序号 · 变量说明」看齐）。
/// <para>
/// 承接两块内容：① 旧「说明」MessageBox 里的通配符（### / * / ?）；② 从批量序号「变量说明」
/// 挪过来的「查找替换 · 变量」一节。变量行做法与 SequenceVariablesWindow 相同：
/// 无边框只读 TextBox 可拖选 / Ctrl+A / Ctrl+C，右侧「复制」按钮一键进剪贴板。
/// </para>
/// </summary>
public partial class ReplaceVariablesWindow : Window
{
    /// <summary>通配符（旧写法，继续支持）。</summary>
    private static readonly (string Variable, string Description)[] Wildcards =
    [
        ("###", "查找：一段数字（第###集 可匹配 第005集）；替换：按出现顺序引用查找里记住的内容"),
        ("*", "查找：任意一段文本"),
        ("?", "查找：任意单个字符"),
    ];

    /// <summary>「查找替换」的变量（查找侧当通配符，替换侧引用 / 变形）。</summary>
    private static readonly (string Variable, string Description)[] RuleVariables =
    [
        ("{英文}", "一段英文字母。例：Abcd001 里的「Abcd」"),
        ("{英文1}", "第 1 段英文，{英文2} 是第 2 段…（按查找里的出现顺序）"),
        ("{数字}", "一段数字。例：Abcd001 里的「001」"),
        ("{中文}", "一段汉字。例：中文名称.ABC 里的「中文名称」"),
        ("{大写英文}", "查找：只匹配大写字母段；替换：把对应的英文段转成大写"),
        ("{小写英文}", "查找：只匹配小写字母段；替换：把对应的英文段转成小写"),
        ("{后缀名=mp4}", "查找：匹配「.mp4」扩展名（不区分大小写）；替换：写 {后缀名=MKV} 输出「.MKV」"),
        ("{英文=ED}", "替换侧带 = 值：直接输出 ED（{数字=01}、{后缀名=MKV} 同理）"),
    ];

    public ReplaceVariablesWindow()
    {
        InitializeComponent();

        foreach (var (variable, description) in Wildcards)
        {
            WildcardRowsHost.Children.Add(BuildRow(variable, description));
        }

        foreach (var (variable, description) in RuleVariables)
        {
            RuleRowsHost.Children.Add(BuildRow(variable, description));
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
