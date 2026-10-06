using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace NameTool.Infrastructure;

/// <summary>
/// 「查找 / 替换」输入框的变量自动补全（附加属性，XAML 里 <c>infra:VariableAutoComplete.IsEnabled="True"</c>）：
/// 输入 <c>{</c> 后自动弹出变量候选下拉（按已输入的前缀过滤，随输入实时刷新），
/// ↑↓ 选择、Enter / Tab 或鼠标点击补全成 <c>{变量名}</c>，Esc 关闭。
/// 多行输入框里弹窗打开时 Enter 被补全占用，关掉弹窗后 Enter 恢复换行。
/// </summary>
public static class VariableAutoComplete
{
    /// <summary>
    /// 候选变量全集：Display=补全后写入的完整文本（含花括号），Filter=匹配输入前缀用的名字（不带花括号）。
    /// 与 ReplaceVariablesWindow 的 RuleVariables 同源——加变量两处同步。
    /// </summary>
    private static readonly (string Display, string Filter, string Desc)[] Candidates =
    [
        ("{英文}", "英文", "一段英文字母；例：Abcd001 里的 Abcd"),
        ("{英文1}", "英文1", "第 1 段英文（按查找里的出现顺序）"),
        ("{英文2}", "英文2", "第 2 段英文；{英文3} 即第 3 段，以此类推"),
        ("{数字}", "数字", "一段数字；例：Abcd001 里的 001"),
        ("{数字1}", "数字1", "第 1 段数字（按查找里的出现顺序）"),
        ("{中文}", "中文", "一段汉字；例：中文名称.ABC 里的 中文名称"),
        ("{中文1}", "中文1", "第 1 段汉字（按查找里的出现顺序）"),
        ("{大写英文}", "大写英文", "查找：只匹配大写字母段；替换：对应英文段转大写"),
        ("{小写英文}", "小写英文", "查找：只匹配小写字母段；替换：对应英文段转小写"),
        ("{后缀名}", "后缀名", "扩展名（含点）；替换侧原样引用当前扩展名"),
        ("{后缀名=mp4}", "后缀名", "查找：匹配 .mp4 扩展名（不分大小写）；替换：输出 .mp4"),
        ("{英文=ED}", "英文", "替换侧带 = 值：直接输出 ED 原文（{数字=01}、{后缀名=MKV} 同理）"),
    ];

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(VariableAutoComplete),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    /// <summary>每个 TextBox 一份弹窗状态（私有附加属性挂靠）。</summary>
    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(State), typeof(VariableAutoComplete),
            new PropertyMetadata(null));

    private sealed class State
    {
        public Popup Popup = null!;
        public ListBox List = null!;
        public TextBlock Hint = null!;
        public int BraceIndex = -1;
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;

        if ((bool)e.NewValue)
        {
            var st = new State();

            var list = new ListBox
            {
                Focusable = false,          // 不抢键盘焦点（焦点始终留在输入框）
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x87, 0xB6, 0xEA)),
                Background = Brushes.White,
                MaxHeight = 224,
                FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            System.Windows.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.ItemContainerStyle = BuildItemContainerStyle();
            list.ItemTemplate = BuildItemTemplate();
            list.PreviewMouseLeftButtonDown += (_, args) =>
            {
                // 鼠标点候选 = 补全（ItemContainer Focusable=false，事件从 ListBox 收）
                if (ResolveItemName(args.OriginalSource as DependencyObject, list) is { } name)
                {
                    Commit(tb, st, name);
                    args.Handled = true;
                }
            };

            var hint = new TextBlock
            {
                Text = "↑↓ 选择 · Enter/点击补全 · Esc 关闭",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6)),
                Margin = new Thickness(8, 3, 8, 4),
            };

            var panel = new StackPanel();
            panel.Children.Add(list);
            panel.Children.Add(hint);

            var border = new Border
            {
                Child = panel,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xB7, 0xD7, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.White,
            };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.25,
            };

            st.List = list;
            st.Hint = hint;
            st.Popup = new Popup
            {
                PlacementTarget = tb,
                Placement = PlacementMode.RelativePoint,
                StaysOpen = false,          // 点输入框以外自动关（焦点还在输入框，打字不受影响）
                AllowsTransparency = true,
                Child = border,
            };

            tb.SetValue(StateProperty, st);
            tb.TextChanged += Tb_TextChanged;
            tb.PreviewKeyDown += Tb_PreviewKeyDown;
            tb.LostKeyboardFocus += (_, _) => Hide(st);
        }
        else
        {
            if (d.GetValue(StateProperty) is not State st) return;
            tb.TextChanged -= Tb_TextChanged;
            tb.PreviewKeyDown -= Tb_PreviewKeyDown;
            st.Popup.IsOpen = false;
            d.SetValue(StateProperty, null);
        }
    }

    private static Style BuildItemContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(new Setter(Border.BorderBrushProperty,
            new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF7))));
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(0xF2, 0xF8, 0xFF))));
        style.Triggers.Add(hoverTrigger);

        return style;
    }

    private static DataTemplate BuildItemTemplate()
    {
        var template = new DataTemplate();

        var panelFactory = new FrameworkElementFactory(typeof(StackPanel));
        panelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var nameFactory = new FrameworkElementFactory(typeof(TextBlock));
        nameFactory.SetBinding(TextBlock.TextProperty, new Binding("."));
        nameFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        nameFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x18, 0x90, 0xFF)));
        nameFactory.SetValue(FrameworkElement.MinWidthProperty, 92.0);
        panelFactory.AppendChild(nameFactory);

        var descFactory = new FrameworkElementFactory(typeof(TextBlock));
        descFactory.SetBinding(TextBlock.TextProperty, new Binding("ToolTip"));
        descFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x5A, 0x6B, 0x80)));
        panelFactory.AppendChild(descFactory);

        template.VisualTree = panelFactory;
        return template;
    }

    private static void Tb_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var st = (State)tb.GetValue(StateProperty);
        if (st is null) return;

        Analyze(tb, st);
    }

    private static void Tb_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var st = (State)tb.GetValue(StateProperty);
        if (st?.Popup.IsOpen != true) return;

        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(st, +1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(st, -1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                if (CommitSelected(tb, st)) e.Handled = true;
                break;
            case Key.Escape:
                Hide(st);
                e.Handled = true;
                break;
        }
    }

    /// <summary>找光标之前最后一个未闭合的 <c>{</c>，按其后的前缀过滤候选并弹 / 收下拉。</summary>
    private static void Analyze(TextBox tb, State st)
    {
        var text = tb.Text;
        var caret = System.Math.Min(tb.CaretIndex, text.Length);

        var brace = -1;
        for (var i = caret - 1; i >= 0; i--)
        {
            if (text[i] == '}') break;      // 已闭合的变量段，不提示
            if (text[i] == '{') { brace = i; break; }
        }

        if (brace < 0)
        {
            Hide(st);
            return;
        }

        var partial = text[(brace + 1)..caret];

        // 已在填 = 值（{后缀名=mp4 的值段）——不再提示变量
        if (partial.Contains('='))
        {
            Hide(st);
            return;
        }

        var matches = Candidates
            .Where(c => partial.Length == 0 || c.Filter.StartsWith(partial, StringComparison.Ordinal))
            .ToArray();

        // 没有候选，或已精确输入到某个候选（刚补全完）——不弹
        if (matches.Length == 0
            || Candidates.Any(c => $"{{{c.Filter}}}" == $"{{{partial}}}"
                                   && partial.Length >= c.Filter.Length))
        {
            Hide(st);
            return;
        }

        st.BraceIndex = brace;
        Show(st, tb, matches);
    }

    private static void Show(State st, TextBox tb, (string Display, string Filter, string Desc)[] matches)
    {
        st.List.Items.Clear();
        foreach (var (display, _, desc) in matches)
        {
            var item = new ListBoxItem { Content = display, Tag = display, ToolTip = desc };
            st.List.Items.Add(item);
        }

        st.List.SelectedIndex = 0;
        st.List.ScrollIntoView(st.List.SelectedItem);
        st.Hint.Visibility = Visibility.Visible;

        // 下拉定位到光标右下方（多行文本按光标所在行的实际坐标）
        var rect = tb.GetRectFromCharacterIndex(tb.CaretIndex);
        st.Popup.HorizontalOffset = System.Math.Max(0, rect.X - 4);
        st.Popup.VerticalOffset = rect.Bottom + 2;

        if (!st.Popup.IsOpen)
        {
            st.Popup.IsOpen = true;
        }
    }

    private static void Hide(State st)
    {
        if (st.Popup.IsOpen)
        {
            st.Popup.IsOpen = false;
        }
        st.List.Items.Clear();
    }

    private static void MoveSelection(State st, int delta)
    {
        if (st.List.Items.Count == 0) return;
        var next = System.Math.Clamp(st.List.SelectedIndex + delta, 0, st.List.Items.Count - 1);
        st.List.SelectedIndex = next;
        st.List.ScrollIntoView(st.List.SelectedItem);
    }

    private static bool CommitSelected(TextBox tb, State st)
    {
        if (st.List.SelectedItem is ListBoxItem { Tag: string name })
        {
            Commit(tb, st, name);
            return true;
        }

        return false;
    }

    /// <summary>把 <c>{已输入前缀</c> 替换成完整变量文本，光标落在末尾。
    /// name 允许两种来源：Item 的 Tag（现含花括号的完整形态，如 <c>{中文}</c>）或纯变量名——
    /// 统一剥掉花括号后按「{ + 内核 + }」组装，避免出现 <c>{{中文}}</c> 双括号。</summary>
    private static void Commit(TextBox tb, State st, string name)
    {
        var caret = System.Math.Min(tb.CaretIndex, tb.Text.Length);
        if (st.BraceIndex < 0 || st.BraceIndex >= caret)
        {
            Hide(st);
            return;
        }

        var core = name.Trim('{', '}');   // 候选 Tag 现在是 "{中文}" 形态，剥壳防双括号
        var before = tb.Text[..st.BraceIndex];
        var after = caret < tb.Text.Length ? tb.Text[caret..] : string.Empty;

        tb.Text = $"{before}{{{core}}}{after}";
        tb.CaretIndex = st.BraceIndex + core.Length + 2;   // { + 内核 + }
        Hide(st);
    }

    /// <summary>从鼠标事件的原始源往上找 ListBoxItem，取出 Tag（变量名，不含花括号）。</summary>
    private static string? ResolveItemName(DependencyObject? source, ListBox list)
    {
        while (source is not null)
        {
            if (source is ListBoxItem item && item.Tag is string name)
            {
                return name;
            }

            if (ReferenceEquals(source, list)) break;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }
}
