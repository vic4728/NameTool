using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NameTool;

/// <summary>
/// 115 网盘「新建文件夹」命名弹窗：标题「新建文件夹命名」，一个名称输入框 + 取消 / 确定。
/// 只管「用户想要什么名字」，**不在这里做合法性判定**（空名只控制按钮可用）：
/// 255 字节上限等校验统一由 ViewModel 在真正创建前做，与改名共用同一份规则。
/// </summary>
public partial class N115NewFolderWindow : Window
{
    /// <summary>用户确认后的文件夹名；取消时为 null。</summary>
    public string? FolderName { get; private set; }

    public N115NewFolderWindow()
    {
        InitializeComponent();
    }

    /// <summary>打开后把焦点给输入框（空名新建，无可预选文本）。</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        NameBox.Focus();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateState();

    /// <summary>输入框内容一变就更新「确定」按钮的可用性：空名（含纯空格）不可提交。</summary>
    private void UpdateState()
    {
        if (ConfirmButton is null || NameBox is null || HintText is null) return;

        var empty = string.IsNullOrWhiteSpace(NameBox.Text);
        ConfirmButton.IsEnabled = !empty;
        HintText.Text = empty
            ? "文件夹名称不能为空。"
            : "文件夹将创建在当前所在的目录里；按 Enter 也能确认。";
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        Confirm();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 「提交」的**决策部分**：能提交就把 <see cref="FolderName"/> 记下来并返回 true。
    /// 与关窗分开写（同 <see cref="N115RenameWindow.TryConfirm"/> 的理由）：给没 ShowDialog 过的窗口
    /// 设 DialogResult 会抛异常，而「能不能提交」本身跟开不开窗无关，离线可断言。
    /// </summary>
    public bool TryConfirm()
    {
        if (ConfirmButton is null || !ConfirmButton.IsEnabled) return false;

        FolderName = NameBox.Text;
        return true;
    }

    /// <summary>提交（按钮与 Enter 共用同一条路径）。</summary>
    private void Confirm()
    {
        if (!TryConfirm()) return;

        DialogResult = true;
    }
}
