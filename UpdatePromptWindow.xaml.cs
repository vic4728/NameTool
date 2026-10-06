using System.Windows;

namespace NameTool;

/// <summary>用户在更新弹窗里的选择。</summary>
public enum UpdatePromptAction
{
    /// <summary>直接关窗（标题栏 ×），不记忽略。</summary>
    None,

    /// <summary>点了「确定」：立即下载更新。</summary>
    Update,

    /// <summary>点了「忽略更新」：记住该版本，启动自动检查不再提示（手动检查仍会弹）。</summary>
    Ignore,
}

/// <summary>
/// 「发现新版本」专用弹窗：显示新版大白话更新内容，按钮 =「确定」（立即更新）/「忽略更新」。
/// 样式与 <see cref="MessageDialogWindow"/> 同一体系。手动点「检查更新」时弹出；
/// 启动自动检查只亮标题栏红字提示，不弹这个窗。
/// </summary>
public partial class UpdatePromptWindow : Window
{
    /// <summary>用户的选择；直接关窗保持 <see cref="UpdatePromptAction.None"/>（不记忽略）。</summary>
    public UpdatePromptAction Action { get; private set; } = UpdatePromptAction.None;

    public UpdatePromptWindow(string newVersion, string currentVersion, string? fileSize,
        System.Collections.Generic.IReadOnlyList<string> notes)
    {
        InitializeComponent();

        HeaderText.Text = string.IsNullOrWhiteSpace(newVersion)
            ? "发现新版本"
            : $"发现新版本 v{newVersion.TrimStart('v')}";

        var sizeText = string.IsNullOrWhiteSpace(fileSize) ? "" : $" · 安装包 {fileSize}";
        SubHeaderText.Text = $"当前版本 v{currentVersion.TrimStart('v')}{sizeText}";

        NotesList.ItemsSource = notes;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Action = UpdatePromptAction.Update;
        Close();
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        Action = UpdatePromptAction.Ignore;
        Close();
    }
}
