using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NameTool;

/// <summary>
/// 115 网盘「重命名」弹窗 —— 只给**单个文件**改名字。
///
/// 交互刻意做得很直白：上面一行是原文件名（只读 TextBox，可拖选 / Ctrl+A / Ctrl+C 复制），
/// 下面一行是新文件名，**打开时就已经预填当前文件名并全选**，
/// 所以「只改其中几个字」的用户可以直接敲字覆盖，不用先手打一遍原名。
///
/// 这里只管「用户想要什么名字」，**不在这里做名称合法性判定**：
/// 空名 / 与原文件同名 / 超 115 的 255 字节上限，统一由 ViewModel 在真正改名时校验，
/// 保证同一套规则只有一份实现（弹窗、批量改名、「繁=>简」共用）。
/// </summary>
public partial class N115RenameWindow : Window
{
    /// <summary>用户确认后的新文件名；取消时为 null。</summary>
    public string? NewName { get; private set; }

    public N115RenameWindow(string currentName)
    {
        InitializeComponent();

        OriginalNameBox.Text = currentName;
        NewNameBox.Text = currentName;
    }

    /// <summary>
    /// 打开后把焦点给输入框并全选：用户想整体换名就直接输入，
    /// 想改局部就按方向键取消选择后编辑。
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        NewNameBox.Focus();
        NewNameBox.SelectAll();
    }

    private void NewNameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateState();

    /// <summary>输入框内容一变就更新「重命名」按钮的可用性，让用户一眼看出能不能提交。</summary>
    private void UpdateState()
    {
        if (RenameButton is null || NewNameBox is null) return;

        var text = NewNameBox.Text;
        var unchanged = text == OriginalNameBox.Text;
        RenameButton.IsEnabled = !string.IsNullOrWhiteSpace(text) && !unchanged;

        HintText.Text = string.IsNullOrWhiteSpace(text)
            ? "新文件名不能为空。"
            : unchanged
                ? "新文件名与原文件名相同，无需重命名。"
                : "改完点「重命名」即可生效；按 Enter 也能确认。";
    }

    private void NewNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        Confirm();
    }

    private void Rename_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 「提交」的**决策部分**：能提交就把 <see cref="NewName"/> 记下来并返回 true。
    /// <para>
    /// 与关窗分开写，是为了让整条流程能离线断言 —— 给没 <c>ShowDialog</c> 过的窗口设
    /// <c>DialogResult</c> 会抛异常，而「填了名字能不能提交」这件事本身跟开不开窗无关。
    /// 按钮点击与 Enter 都先走这里，行为不会有分叉。
    /// </para>
    /// </summary>
    public bool TryConfirm()
    {
        if (RenameButton is null || !RenameButton.IsEnabled) return false;

        NewName = NewNameBox.Text;
        return true;
    }

    /// <summary>提交（按钮与 Enter 共用同一条路径）。</summary>
    private void Confirm()
    {
        if (!TryConfirm()) return;

        DialogResult = true;
    }
}
