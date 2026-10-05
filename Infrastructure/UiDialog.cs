using System.Linq;
using System.Windows;

namespace NameTool.Infrastructure;

/// <summary>
/// 统一弹窗门面：全程序的提示 / 确认都走这里，样式与主窗口对齐（<see cref="MessageDialogWindow"/>）。
/// API 与 <c>MessageBox.Show</c> 兼容（返回 <see cref="MessageBoxResult"/>），调用点只改方法名。
/// <para>
/// ⚠️ <see cref="PromptsEnabled"/> 默认 <c>false</c>：离屏校验 / 无界面环境直接返回保守默认值，
/// 绝不创建窗口（系统 MessageBox 会把无头断言卡死）。<see cref="MainWindow"/> 构造时打开。
/// </para>
/// </summary>
public static class UiDialog
{
    /// <summary>是否真的弹窗。有界面的入口（MainWindow）构造时设 true。</summary>
    public static bool PromptsEnabled { get; set; }

    /// <summary>与 MessageBox.Show(this, …) 形态对应：第一个参数是 owner。</summary>
    public static MessageBoxResult Show(
        Window owner, string message, string title = "",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        => ShowCore(message, title, buttons, icon, owner, MessageBoxResult.OK);

    /// <summary>与 MessageBox.Show(…) 形态对应：owner 自动取当前活动窗口 / 主窗口；
    /// <paramref name="defaultResult"/> 对应 MessageBox 的默认按钮参数（回车落在哪颗上）。</summary>
    public static MessageBoxResult Show(
        string message, string title = "",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.OK)
        => ShowCore(message, title, buttons, icon, null, defaultResult);

    private static MessageBoxResult ShowCore(
        string message, string title, MessageBoxButton buttons, MessageBoxImage icon, Window? owner,
        MessageBoxResult defaultResult)
    {
        if (!PromptsEnabled)
        {
            // 离屏 / 无界面：保守默认值（不放行确认类），并保证不创建任何窗口
            return buttons switch
            {
                MessageBoxButton.OK => MessageBoxResult.OK,
                MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
                MessageBoxButton.YesNo => MessageBoxResult.No,
                _ => MessageBoxResult.Cancel,
            };
        }

        var dialog = new MessageDialogWindow(message, title, buttons, icon, defaultResult)
        {
            Owner = owner ?? ResolveOwner(),
        };
        dialog.ShowDialog();
        return dialog.Result;
    }

    /// <summary>ViewModel 里拿不到 owner：优先当前活动窗口，退回主窗口。</summary>
    private static Window? ResolveOwner()
        => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
           ?? Application.Current?.MainWindow;
}
