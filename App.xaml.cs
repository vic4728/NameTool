using System.Windows;
using NameTool.Services;

namespace NameTool;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 「繁=>简」词典（约 4800 条 / 45KB 内嵌资源）提前在后台读进来，
        // 免得首次点按钮时才解析，白等一下。加载失败不影响启动 ——
        // 真正点按钮时会照常抛错，问题不会被藏住。
        _ = Task.Run(ChineseConverter.WarmUp);
    }
}
