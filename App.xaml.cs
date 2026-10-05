using System.Windows;
using NameTool.Services;

namespace NameTool;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        // WPF 默认用「Adorner 覆盖层」画文字选区：半透明矩形盖在文字**上面**，
        // 不透明高亮（如查找命中的 #FFFF26 黄底）会把文字整个盖死，SelectionTextBrush 也不生效。
        // 关掉这个开关 ⇒ 选区画在文字**下层**，SelectionTextBrush（黑字）真正生效。
        // 必须在任何 WPF 界面创建之前设置（App 构造函数是唯一可靠时机）。
        AppContext.SetSwitch("Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering", false);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 「繁=>简」词典（约 4800 条 / 45KB 内嵌资源）提前在后台读进来，
        // 免得首次点按钮时才解析，白等一下。加载失败不影响启动 ——
        // 真正点按钮时会照常抛错，问题不会被藏住。
        _ = Task.Run(ChineseConverter.WarmUp);
    }
}
