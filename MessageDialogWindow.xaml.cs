using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NameTool;

/// <summary>
/// 统一消息对话框：替代系统 <c>MessageBox</c>，样式与主窗口对齐（浅蓝底、圆角、蓝主按钮）。
/// 一般不要直接用 —— 走 <see cref="UiDialog.Show"/> 门面（它处理离屏环境与 owner 解析）。
/// </summary>
public partial class MessageDialogWindow : Window
{
    /// <summary>用户点了哪个按钮；直接关窗（标题栏 ×）时保持 None（按「默认按钮」语义由调用方决定）。</summary>
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public MessageDialogWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.OK)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(title) ? "提示" : title;
        MessageText.Text = message;
        _defaultResult = defaultResult;

        ApplyIcon(icon);
        BuildButtons(buttons);

        // 标题栏 × 直接关窗：按「默认按钮」的结果返回（与系统 MessageBox 的 default-button 语义一致）
        Closing += (_, _) =>
        {
            if (Result == MessageBoxResult.None) Result = _defaultResult;
        };
    }

    private readonly MessageBoxResult _defaultResult;

    private void ApplyIcon(MessageBoxImage icon)
    {
        // 颜色分档与主窗口语义色一致：信息/询问=蓝 #1890FF，警告=橙 #F5821F，错误=红 #E74C3C
        var (glyph, color) = icon switch
        {
            MessageBoxImage.Error => ("\uE783", "#E74C3C"),
            MessageBoxImage.Warning => ("\uE7BA", "#F5821F"),
            MessageBoxImage.Question => ("\uE897", "#1890FF"),
            MessageBoxImage.Information => ("\uE946", "#1890FF"),
            _ => (string.Empty, string.Empty),
        };

        if (string.IsNullOrEmpty(glyph))
        {
            // 无图标：把图标列收掉，正文顶格
            IconColumn.Width = new GridLength(0);
            IconCircle.Visibility = Visibility.Collapsed;
            return;
        }

        IconGlyph.Text = glyph;
        IconCircle.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void BuildButtons(MessageBoxButton buttons)
    {
        // 主按钮 = 确定 / 是（蓝底白字、回车触发）；取消类按钮挂 IsCancel（Esc 触发），
        // 与系统 MessageBox 的键位习惯一致。
        var (primary, secondary, cancelSlot) = buttons switch
        {
            MessageBoxButton.OK => (new[] { ("确定", MessageBoxResult.OK) },
                Array.Empty<(string, MessageBoxResult)>(), -1),
            MessageBoxButton.OKCancel => (new[] { ("确定", MessageBoxResult.OK) },
                new[] { ("取消", MessageBoxResult.Cancel) }, 1),
            MessageBoxButton.YesNo => (new[] { ("是", MessageBoxResult.Yes) },
                new[] { ("否", MessageBoxResult.No) }, -1),
            _ => (new[] { ("是", MessageBoxResult.Yes) },
                new[] { ("否", MessageBoxResult.No), ("取消", MessageBoxResult.Cancel) }, 2),
        };

        var slot = 0;
        foreach (var (text, result) in primary)
        {
            AddButton(text, result, isPrimary: true, slot++, isCancel: false);
        }

        foreach (var (text, result) in secondary)
        {
            var current = slot++;
            AddButton(text, result, isPrimary: false, current, isCancel: current == cancelSlot);
        }
    }

    private void AddButton(string text, MessageBoxResult result, bool isPrimary, int slot, bool isCancel)
    {
        var button = new Button
        {
            Content = text,
            Width = 84,
            Height = 30,
            Padding = new Thickness(0),
            Tag = result,
            Style = (Style)Resources[isPrimary ? "DialogPrimaryButtonStyle" : "DialogSecondaryButtonStyle"],
        };

        if (slot > 0)
        {
            button.Margin = new Thickness(8, 0, 0, 0);
        }

        if (isPrimary)
        {
            button.IsDefault = true;
        }

        // 回车触发哪颗按钮由调用方指定（如 115 删除确认把默认按钮放在「否」上防误删）
        if (result == _defaultResult)
        {
            button.IsDefault = true;
        }

        if (isCancel)
        {
            button.IsCancel = true;
        }

        button.Click += (_, _) =>
        {
            Result = result;
            Close();
        };

        ButtonPanel.Children.Add(button);
    }
}
