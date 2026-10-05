using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NameTool.Services.N115;
using NameTool.ViewModels;
using NameTool.Infrastructure;

namespace NameTool;

/// <summary>
/// 「移动到 / 复制到」的目标文件夹选择器。
///
/// 交互：
/// <list type="bullet">
/// <item>双击文件夹进入下一级；</item>
/// <item><b>点路径栏上的任意一节直接跳到那一层</b>（不用逐层点「返回上级」）；</item>
/// <item><b>打开时自动回到上次用过的目标目录</b>（按「移动 / 复制」分别记，见
/// <see cref="N115PickerMemoryStore"/>）；</item>
/// <item>「确定」把内容放进当前所在那一层。</item>
/// </list>
///
/// 正在被移动/复制的那些文件夹自身（<c>forbiddenIds</c>）不允许被进入，
/// 因为进入它的子树就等于把文件夹移进它自己 —— 用「禁止进入」而不是事后校验，
/// 用户根本走不到非法的目标上。
/// </summary>
public partial class N115FolderPickerWindow : Window
{
    private readonly N115Client _client;
    private readonly string _title;

    /// <summary>本次动作（移动 / 复制）：标题、文案、以及「上次目标目录」的记忆键都由它派生。</summary>
    private readonly N115TransferKind _kind;

    private readonly N115PickerMemoryStore _memory = new();
    private readonly HashSet<string> _forbidden;
    private readonly List<(string Id, string Name)> _path = [];
    private readonly ObservableCollection<N115ItemViewModel> _folders = [];
    private readonly ObservableCollection<N115CrumbViewModel> _crumbs = [];

    /// <summary>调用方给的起点（当前浏览目录）。记忆失效时回退到这里。</summary>
    private readonly List<(string Id, string Name)> _fallbackPath = [];

    /// <summary>当前路径是否来自「上次目标目录」的记忆。用户一旦自己导航就置 false。</summary>
    private bool _usingRememberedStart;

    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>最近一次目录加载结果：true 成功 / false 明确失败 / null 被取消。</summary>
    private bool? _lastLoadOk;

    public N115FolderPickerWindow(
        N115Client client,
        IReadOnlyList<(string Id, string Name)> startPath,
        N115TransferKind kind,
        IReadOnlyList<string> forbiddenIds)
    {
        InitializeComponent();

        _client = client;
        _kind = kind;
        _title = $"{_kind.ToVerb()}到";

        _forbidden = [.. forbiddenIds];

        Title = _title;
        FolderList.ItemsSource = _folders;
        PathCrumbs.ItemsSource = _crumbs;

        foreach (var entry in startPath) _fallbackPath.Add(entry);
        if (_fallbackPath.Count == 0) _fallbackPath.Add(("0", "根目录"));

        // 打开时优先回到上次用过的目标目录；没有记忆（或记忆不可用）就用调用方给的起点。
        // 「记忆的目录是否还在」只能靠真正加载一次来判断，所以这里先按记忆摆好路径，
        // 由 LoadInitialAsync 负责在加载失败时回退（见那里）。
        var remembered = _memory.Load(_kind.ToKey());
        if (remembered is { Count: > 0 })
        {
            foreach (var segment in remembered) _path.Add((segment.Id, segment.Name));
            _usingRememberedStart = true;
        }
        else
        {
            foreach (var entry in _fallbackPath) _path.Add(entry);
        }

        Loaded += async (_, _) => await LoadInitialAsync();

        // 路径栏与按钮状态**在构造时就摆好**，不等首次 Loaded：
        // 一来面包屑属于「静态 chrome」，没有理由跟着目录列表一起等；
        // 二来这样窗口一出现路径和「上次使用的位置」提示就是最终的，不会闪一下再变。
        RefreshChrome();
    }

    /// <summary>用户确认后的选择；取消则为 null。</summary>
    public N115PickerResult? Result { get; private set; }

    /// <summary>当前路径链（诊断 / 校验用）。</summary>
    public IReadOnlyList<(string Id, string Name)> PathSegments => _path.ToList();

    /// <summary>当前是否停在「上次用过的目标目录」上（诊断 / 校验用）。</summary>
    public bool IsUsingRememberedStart => _usingRememberedStart;

    /// <summary>路径栏上的面包屑（诊断 / 校验用）。</summary>
    public IReadOnlyList<N115CrumbViewModel> Crumbs => _crumbs;

    private string CurrentId => _path.Count > 0 ? _path[^1].Id : "0";

    private string CurrentName => _path.Count > 0 ? _path[^1].Name : "根目录";

    /// <summary>
    /// 纯函数：点路径栏上 <paramref name="crumbId"/> 那一节之后，路径应该变成什么。
    /// <para>
    /// 返回 null 表示**不跳**：该节不在当前路径上（理论上不会发生），
    /// 或者点的正是当前所在层（重新拉一遍列表没意义）。
    /// </para>
    /// <para>
    /// 抽成纯函数是为了能离线断言（事件处理器本身要 Button + 已渲染的模板才能触发）。
    /// </para>
    /// </summary>
    public static List<(string Id, string Name)>? PathAfterCrumbJump(
        IReadOnlyList<(string Id, string Name)> path, string crumbId)
    {
        var index = -1;
        for (var i = 0; i < path.Count; i++)
        {
            if (!string.Equals(path[i].Id, crumbId, StringComparison.Ordinal)) continue;
            index = i;
            break;
        }

        if (index < 0 || index == path.Count - 1) return null;

        return path.Take(index + 1).ToList();
    }

    // ---------------- 首次加载（含记忆回退） ----------------

    /// <summary>
    /// 打开后的第一次加载。用记忆的目录试一次，**失败就忘掉它并回到调用方给的起点** ——
    /// 记忆里的目录可能已经被删掉 / 改名 / 换了账号，那种情况下必须能自动兜回来，
    /// 否则窗口会停在一个读不出内容的空目录上。
    /// </summary>
    private async Task LoadInitialAsync()
    {
        if (!_usingRememberedStart)
        {
            await LoadAsync(CurrentId);
            return;
        }

        await LoadAsync(CurrentId);
        if (_lastLoadOk != false) return;   // 成功，或被取消（不当成失效，免得误清记忆）

        _usingRememberedStart = false;
        _memory.Clear(_kind.ToKey());

        _path.Clear();
        foreach (var entry in _fallbackPath) _path.Add(entry);
        await LoadAsync(CurrentId);
    }

    // ---------------- 导航 ----------------

    /// <summary>用户自己动了导航 ⇒ 不再是「打开时的记忆位置」，提示也就可以收起来了。</summary>
    private void MarkNavigated() => _usingRememberedStart = false;

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_path.Count <= 1 || _busy) return;

        MarkNavigated();
        _path.RemoveAt(_path.Count - 1);
        _ = LoadAsync(CurrentId);
    }

    private void Root_Click(object sender, RoutedEventArgs e)
    {
        if (_path.Count <= 1 || _busy) return;

        MarkNavigated();
        _path.RemoveRange(1, _path.Count - 1);
        _ = LoadAsync(CurrentId);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _ = LoadAsync(CurrentId);
    }

    /// <summary>
    /// 点路径栏上的一节 ⇒ 直接跳到那一层（把后面的层截掉）。
    /// 点最后一节（即当前所在层）不做事，避免无谓地重新拉一遍列表。
    /// </summary>
    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button { DataContext: N115CrumbViewModel crumb }) return;

        var jumped = PathAfterCrumbJump(_path, crumb.Id);
        if (jumped is null) return;

        MarkNavigated();
        _path.Clear();
        _path.AddRange(jumped);
        _ = LoadAsync(CurrentId);
    }

    private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is not N115ItemViewModel folder || !folder.IsDirectory) return;
        if (_forbidden.Contains(folder.Id))
        {
            UiDialog.Show(this,
                $"「{folder.Name}」正是本次要{_kind.ToVerb()}的文件夹，不能把它放到它自己或它的子目录里。",
                _title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MarkNavigated();
        _path.Add((folder.Id, folder.Name));
        _ = LoadAsync(folder.Id);
    }

    private async Task LoadAsync(string cid)
    {
        var ct = ResetCts();
        _busy = true;
        RefreshChrome();
        StatusText.Text = "正在读取目录...";
        _folders.Clear();
        EmptyText.Visibility = Visibility.Collapsed;
        _lastLoadOk = null;

        try
        {
            var page = await _client.ListAsync(cid, 0, N115Client.MaxPageSize, ct);
            foreach (var info in page.Data ?? [])
            {
                var item = N115ItemViewModel.FromInfo(info);
                if (!item.IsDirectory) continue;
                if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name)) continue;
                _folders.Add(item);
            }

            var ordered = _folders
                .OrderBy(x => x.Name, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), true))
                .ToList();
            _folders.Clear();
            foreach (var item in ordered) _folders.Add(item);

            EmptyText.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"共 {_folders.Count} 个子文件夹";
            _lastLoadOk = true;
        }
        catch (OperationCanceledException)
        {
            // 被后一次导航取消了：既不算成功也不算失败，别据此把记忆判为失效
            _lastLoadOk = null;
        }
        catch (N115ApiException ex)
        {
            StatusText.Text = ex.Message;
            _lastLoadOk = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取目录失败：{ex.Message}";
            _lastLoadOk = false;
        }
        finally
        {
            _busy = false;
            RefreshChrome();
        }
    }

    private CancellationToken ResetCts()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    private void RefreshChrome()
    {
        RebuildCrumbs();

        var canGoUp = _path.Count > 1 && !_busy;
        UpButton.IsEnabled = canGoUp;
        RootButton.IsEnabled = canGoUp;
        RefreshButton.IsEnabled = !_busy;

        // 当前所在目录若正是待移动的文件夹本身，就不能作为目标
        var illegal = _forbidden.Contains(CurrentId);

        // 按钮上只写「确定」——目标目录名交给上方路径栏与左侧的「目标：…」，按钮里重复一遍
        // 既冗余、长目录名又会把按钮撑得忽宽忽窄（改短后靠 XAML 的 MinWidth 稳住宽度）。
        ConfirmButton.Content = illegal ? "不能选这里" : "确定";
        ConfirmButton.IsEnabled = !_busy && !illegal;

        TargetText.Text = illegal
            ? "当前目录是本次待操作的文件夹本身，请换一层"
            : $"目标：{CurrentName}";

        // 记忆生效时标一下来源，免得用户疑惑「怎么不是从当前目录开始」
        RememberHint.Visibility = _usingRememberedStart ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>按当前路径重建面包屑（每一节都是可点的按钮）。</summary>
    private void RebuildCrumbs()
    {
        _crumbs.Clear();
        for (var i = 0; i < _path.Count; i++)
        {
            _crumbs.Add(new N115CrumbViewModel
            {
                Id = _path[i].Id,
                Name = _path[i].Name,
                ShowSeparator = i > 0,
            });
        }

        // 深层路径下用户最关心当前位置 ⇒ 滚到最右端。
        // 必须等布局完成，否则内容宽度还是 0、滚不到位。
        if (PathScroll is not null && IsLoaded)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => PathScroll.ScrollToRightEnd()));
        }
    }

    // ---------------- 确认 / 取消 ----------------

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_forbidden.Contains(CurrentId))
        {
            UiDialog.Show(this, "不能把文件夹放到它自己里面。", _title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var segments = _path.Select(x => new N115PickerPathSegment(x.Id, x.Name)).ToList();

        // 用户确认的这一层才算「上次用过的目标目录」——选择过程中路过的不算
        _memory.Save(_kind.ToKey(), segments);

        Result = new N115PickerResult(
            CurrentId,
            CurrentName,
            string.Join(" > ", _path.Select(x => x.Name)),
            _path.Select(x => x.Id).ToList(),
            segments);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnClosed(e);
    }
}
