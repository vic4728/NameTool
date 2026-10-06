using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using NameTool.Infrastructure;
using NameTool.Services;
using NameTool.Services.N115;

namespace NameTool.ViewModels;

/// <summary>
/// 按右侧功能栏规则算出新名称；返回 Error 表示参数不合法或结果为空。
/// <paramref name="isDirectory"/> 为 true 表示当前处理的是文件夹（整名作主体，不拆扩展名）。
/// <paramref name="dateText"/> 是条目的修改日期（yyyy-MM-dd，未知为 null），给模板变量 <c>{date}</c> 用。
/// </summary>
public delegate (string NewName, string? Error) N115NameBuilder(string originalName, int index, bool isDirectory, string? dateText);

/// <summary>115 列表可点击表头排序的列（与列的 SortMemberPath 同名）。</summary>
public enum N115SortColumn
{
    Name,
    Type,
    Size,
    Time,
}

/// <summary>
/// 115 网盘浏览 / 批量改名。目录与文件走 115 网页版接口，
/// 改名规则完全复用右侧功能栏（批量替换 + 文件名添加/删除 + 批量序号）。
/// </summary>
public sealed class N115ViewModel : ObservableObject
{
    private const int PageSize = 1000;
    private const string RootId = "0";
    private const string RootName = "根目录";

    /// <summary>
    /// 改名请求之间的额外间隔。客户端 <see cref="N115Client"/> 内已有统一节流（250ms），
    /// 这里不再叠加等待；若要更保守，把它调大即可。
    /// </summary>
    private static readonly TimeSpan RenameThrottle = TimeSpan.Zero;

    /// <summary>115 对文件/文件夹名的长度上限（按 UTF-8 字节数计）。</summary>
    private const int MaxNameBytes = 255;

    private readonly N115CredentialStore _store = new();
    private readonly N115NameBuilder _nameBuilder;
    private readonly Action<string> _log;

    private readonly List<N115ItemViewModel> _buffer = [];
    private readonly List<(string Id, string Name)> _path = [];
    private readonly List<(string FileId, string OldName, N115ItemViewModel Item)> _renameRollback = [];

    private N115Client? _client;
    private IReadOnlyList<N115ItemViewModel> _selection = [];
    private CancellationTokenSource? _cts;
    private int _totalCount;

    /// <summary>当前排序列与方向。文件夹组、文件组各自按这一组规则排，互不影响。</summary>
    private N115SortColumn _sortColumn = N115SortColumn.Name;
    private bool _sortDescending;

    /// <summary>排序状态变化（含打开目录时的「默认升序」重置），供窗口同步列头箭头。</summary>
    public event Action? SortChanged;

    /// <summary>当前排序列（给窗口画箭头用）。</summary>
    public N115SortColumn CurrentSortColumn => _sortColumn;

    /// <summary>当前是否降序（给窗口画箭头用）。</summary>
    public bool IsSortDescending => _sortDescending;

    /// <summary>
    /// 名称排序：自然排序（数字段按数值比，「副本 (2)」排在「副本 (10)」前面），
    /// 文字段 zh-CN 拼音、忽略大小写 —— 与本地列表共用 <see cref="NaturalNameComparer"/>。
    /// </summary>
    private static readonly NaturalNameComparer NameComparer = NaturalNameComparer.Instance;

    private bool _isActive;
    private bool _isBusy;
    private bool _hasMore;
    private bool _isEmpty = true;
    private string _statusText = string.Empty;
    private string _selectionSummary = "未选择";
    private string _renameButtonText = "按右侧规则重命名";
    private string _accountText = "未登录";

    public N115ViewModel(N115NameBuilder nameBuilder, Action<string> log)
    {
        _nameBuilder = nameBuilder;
        _log = log;

        EnterCommand = new AsyncRelayCommand(_ => EnterAsync(), _ => !IsBusy);
        GoUpCommand = new RelayCommand(_ => GoUp(), _ => _path.Count > 1);
        GoRootCommand = new RelayCommand(_ => GoRoot(), _ => _path.Count > 1);
        GoToCrumbCommand = new RelayCommand(p => GoToCrumb(p as N115CrumbViewModel), p => p is N115CrumbViewModel);
        RefreshCommand = new RelayCommand(_ => Refresh(), _ => _client is not null);
        LoadMoreCommand = new RelayCommand(_ => LoadMore(), _ => !IsBusy && HasMore);
        EnterFolderCommand = new RelayCommand(p => EnterFolder(p as N115ItemViewModel), p => p is N115ItemViewModel { IsDirectory: true });
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => !IsBusy && _selection.Count > 0);
        SelectFilesCommand = new RelayCommand(_ => RequestSelect(false), _ => !IsBusy && Items.Count > 0);
        SelectFoldersCommand = new RelayCommand(_ => RequestSelect(true), _ => !IsBusy && Items.Count > 0);
        RenameSelectedCommand = new AsyncRelayCommand(_ => RenameSelectedAsync(), _ => !IsBusy && _selection.Count > 0);
        ToSimplifiedCommand = new AsyncRelayCommand(_ => RenameSelectedToSimplifiedAsync(), _ => !IsBusy && _selection.Count > 0);
        UndoRenameCommand = new AsyncRelayCommand(_ => UndoRenameAsync(), _ => !IsBusy && _renameRollback.Count > 0);
        // 「重命名」只针对**单个文件**：选中恰好 1 项、且它不是文件夹时才可用（文件夹请用底部那条按规则改名）
        RenameSingleCommand = new AsyncRelayCommand(_ => RenameSingleAsync(), _ => CanRenameSingle);
        MoveToCommand = new AsyncRelayCommand(_ => TransferAsync(move: true), _ => !IsBusy && _selection.Count > 0);
        CopyToCommand = new AsyncRelayCommand(_ => TransferAsync(move: false), _ => !IsBusy && _selection.Count > 0);
        CreateFolderCommand = new AsyncRelayCommand(_ => CreateFolderAsync(), _ => !IsBusy);
        DeleteSelectedCommand = new AsyncRelayCommand(_ => DeleteSelectedAsync(), _ => !IsBusy && _selection.Count > 0);
        ReloginCommand = new AsyncRelayCommand(_ => ReloginAsync(), _ => !IsBusy);
        LogoutCommand = new RelayCommand(_ => Logout(), _ => !IsBusy);
        ExitCommand = new RelayCommand(_ => Exit());
    }

    /// <summary>由宿主窗口注入：弹出登录窗口，返回 null 表示用户取消。</summary>
    public Func<Task<N115Credential?>>? LoginPrompt { get; set; }

    /// <summary>由宿主窗口注入：弹出「移动到 / 复制到」的目标文件夹选择器。</summary>
    public N115FolderPickerAsync? FolderPicker { get; set; }

    /// <summary>
    /// 由宿主窗口注入：弹出单文件「重命名」弹窗，入参是当前文件名，返回用户输入的新名字；
    /// 返回 null 表示用户取消。放在宿主里弹窗，ViewModel 才能离线跑完整条链路（注入假弹窗即可）。
    /// </summary>
    public Func<string, string?>? RenamePrompt { get; set; }

    /// <summary>由宿主窗口注入：弹出「新建文件夹命名」弹窗，返回用户输入的目录名；null 表示用户取消。</summary>
    public Func<string?>? NewFolderPrompt { get; set; }

    /// <summary>当前所在目录链（选择器用它作为起点，保证面包屑完整）。</summary>
    public IReadOnlyList<(string Id, string Name)> CurrentPath => _path.ToList();

    public ObservableCollection<N115ItemViewModel> Items { get; } = [];
    public ObservableCollection<N115CrumbViewModel> Crumbs { get; } = [];

    public AsyncRelayCommand EnterCommand { get; }
    public RelayCommand GoUpCommand { get; }
    public RelayCommand GoRootCommand { get; }
    public RelayCommand GoToCrumbCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand LoadMoreCommand { get; }
    public RelayCommand EnterFolderCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand SelectFilesCommand { get; }
    public RelayCommand SelectFoldersCommand { get; }
    public AsyncRelayCommand RenameSelectedCommand { get; }

    /// <summary>「繁=&gt;简」：把选中条目名称里的繁体字转成简体（只改名字，不读也不写网盘上的文件内容）。</summary>
    public AsyncRelayCommand ToSimplifiedCommand { get; }

    public AsyncRelayCommand UndoRenameCommand { get; }

    /// <summary>
    /// 顶部按钮行里的「重命名」：**只对单个文件**弹窗改名，不套任何规则。
    /// 与底部「按规则重命名」（批量、走右侧规则）是两件事，别混。
    /// </summary>
    public AsyncRelayCommand RenameSingleCommand { get; }
    public AsyncRelayCommand MoveToCommand { get; }
    public AsyncRelayCommand CopyToCommand { get; }
    public AsyncRelayCommand CreateFolderCommand { get; }
    public AsyncRelayCommand DeleteSelectedCommand { get; }
    public AsyncRelayCommand ReloginCommand { get; }
    public RelayCommand LogoutCommand { get; }
    public RelayCommand ExitCommand { get; }

    /// <summary>是否正处于 115 网盘浏览模式（决定界面上显示本地工具栏还是网盘工具栏）。</summary>
    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetProperty(ref _isActive, value))
            {
                RaisePropertyChanged(nameof(IsLocalMode));
            }
        }
    }

    public bool IsLocalMode => !_isActive;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandsCanExecute();
            }
        }
    }

    public bool HasMore
    {
        get => _hasMore;
        private set
        {
            if (SetProperty(ref _hasMore, value))
            {
                LoadMoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>当前目录是否一项都没有（用于在列表区域显示空目录提示）。</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    /// <summary>当前所在目录的完整路径，用于状态提示与日志。</summary>
    private string CurrentPathText =>
        _path.Count == 0 ? RootName : string.Join(" > ", _path.Select(x => x.Name));

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>状态行文案（宿主窗口在网盘命令不可用时提示用户；如「请先选中要改名的条目」）。</summary>
    public void SetStatusText(string text) => StatusText = text;

    public string SelectionSummary
    {
        get => _selectionSummary;
        private set => SetProperty(ref _selectionSummary, value);
    }

    /// <summary>改名按钮文案，随选中内容是文件还是文件夹变化。</summary>
    public string RenameButtonText
    {
        get => _renameButtonText;
        private set => SetProperty(ref _renameButtonText, value);
    }

    public string AccountText
    {
        get => _accountText;
        private set => SetProperty(ref _accountText, value);
    }

    // ---------------- 进入 / 退出 ----------------

    public async Task EnterAsync()
    {
        if (IsBusy) return;

        if (_client is null)
        {
            var saved = _store.Load();
            if (saved is not null)
            {
                SetClient(saved);
                using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var outcome = await _client!.ProbeCredentialAsync(probe.Token);
                if (outcome == N115ProbeOutcome.AuthExpired)
                {
                    _log("[115] 本地保存的登录状态已失效，需要重新登录");
                    DisposeClient();
                }
                else if (outcome == N115ProbeOutcome.Inconclusive)
                {
                    // 探测失败 ≠ 登录失效（网络波动 / WAF 拦截 / 探测端点下线都可能）。
                    // 先按已登录继续；真失效时第一个实际接口会报 auth 错误，走 HandleApiError。
                    _log("[115] 暂时无法确认保存的登录状态（网络原因），先按已登录继续");
                }
                else
                {
                    _log($"[115] 已恢复本地保存的登录：{saved.DisplayName}");
                }
            }
        }

        if (_client is null && !await PromptLoginAsync()) return;

        IsActive = true;
        _path.Clear();
        _path.Add((RootId, RootName));
        await LoadDirectoryAsync(RootId);
    }

    public void Exit()
    {
        _cts?.Cancel();
        IsActive = false;
        ClearSelection();
    }

    private async Task<bool> PromptLoginAsync()
    {
        if (LoginPrompt is null) return false;

        var credential = await LoginPrompt();
        if (credential is null) return false;

        _store.Save(credential);
        SetClient(credential);
        _log($"[115] 已登录：{credential.DisplayName}");
        return true;
    }

    private async Task ReloginAsync()
    {
        if (LoginPrompt is null) return;

        DisposeClient();
        if (!await PromptLoginAsync()) return;

        _path.Clear();
        _path.Add((RootId, RootName));
        await LoadDirectoryAsync(RootId);
    }

    private void Logout()
    {
        if (UiDialog.Show("退出登录会清除本机保存的 115 登录状态，下次需要重新登录授权。\n\n确认退出？",
                "115 网盘", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        DisposeClient();
        _store.Clear();
        _buffer.Clear();
        Items.Clear();
        Crumbs.Clear();
        _path.Clear();
        ClearSelection();
        IsActive = false;
        AccountText = "未登录";
        StatusText = "已退出登录";
        _log("[115] 已退出登录");
    }

    private void SetClient(N115Credential credential)
    {
        DisposeClient();
        _client = new N115Client();
        _client.UseCredential(credential);
        AccountText = credential.DisplayName;
    }

    private void DisposeClient()
    {
        _client?.Dispose();
        _client = null;
    }

    // ---------------- 目录浏览 ----------------

    private CancellationToken ResetCts()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    private async Task LoadDirectoryAsync(string cid)
    {
        var client = _client;
        if (client is null) return;

        var ct = ResetCts();
        IsBusy = true;
        StatusText = "正在读取目录...";

        // 每次打开目录都回到「按名称升序」（用户要求：进目录默认升序），
        // 上个目录里点过的降序 / 按大小等选择不带到新目录。
        _sortColumn = N115SortColumn.Name;
        _sortDescending = false;
        SortChanged?.Invoke();

        _buffer.Clear();
        _totalCount = 0;

        // 先把列表清空再请求，避免切换目录失败时残留上一个目录的内容造成误解
        RebuildItems();

        try
        {
            var page = await client.ListAsync(cid, 0, PageSize, ct);
            var rawCount = page.Data?.Count ?? 0;
            AppendItems(page);
            _totalCount = page.Count > 0 ? page.Count : _buffer.Count;
            RebuildItems();
            RebuildCrumbs();
            HasMore = _buffer.Count < _totalCount;

            if (_buffer.Count == 0)
            {
                // 区分「真的空目录」与「接口有数据但一条都没解析出来」，
                // 后者说明 115 调整了字段，需要按日志排查而不是让用户对着空列表发呆。
                StatusText = rawCount > 0
                    ? $"该目录返回了 {rawCount} 条记录但均无法识别，请把日志反馈给作者（路径：{CurrentPathText}）"
                    : $"{CurrentPathText} 为空（共 0 项）";
                _log($"[115] {CurrentPathText} 为空：cid={cid}，接口返回 {rawCount} 条，解析 {_buffer.Count} 条");
            }
            else
            {
                StatusText = $"{CurrentPathText}：共 {_totalCount} 项，已载入 {_buffer.Count} 项";
                _log($"[115] 打开 {CurrentPathText}（cid={cid}）：接口 {rawCount} 条，显示 {_buffer.Count} 项");
            }
        }
        catch (OperationCanceledException)
        {
            // 切换目录/退出时取消，忽略
        }
        catch (N115ApiException ex)
        {
            HandleApiError(ex);
        }
        catch (Exception ex)
        {
            StatusText = $"读取目录失败：{ex.Message}";
            _log($"[115] 读取目录失败（{CurrentPathText}，cid={cid}）：{ex}");
        }
        finally
        {
            IsBusy = false;
            ClearSelection();
        }
    }

    private void LoadMore()
    {
        if (_client is null || !HasMore) return;

        var ct = ResetCts();
        IsBusy = true;
        StatusText = "正在加载更多...";

        _ = LoadMoreCoreAsync(ct);
    }

    private async Task LoadMoreCoreAsync(CancellationToken ct)
    {
        var client = _client;
        if (client is null) return;

        try
        {
            var cid = CurrentFolderId;
            var page = await client.ListAsync(cid, _buffer.Count, PageSize, ct);
            AppendItems(page);
            if (page.Count > 0) _totalCount = page.Count;
            RebuildItems();
            HasMore = _buffer.Count < _totalCount;
            StatusText = $"{CurrentPathText}：共 {_totalCount} 项，已载入 {_buffer.Count} 项";
        }
        catch (OperationCanceledException)
        {
        }
        catch (N115ApiException ex)
        {
            HandleApiError(ex);
        }
        catch (Exception ex)
        {
            StatusText = $"加载更多失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Refresh()
    {
        if (_client is null) return;

        var cid = CurrentFolderId;
        _ = LoadDirectoryAsync(cid);
    }

    private void AppendItems(N115FileListResponse page)
    {
        if (page.Data is null || page.Data.Count == 0) return;

        foreach (var info in page.Data)
        {
            var item = N115ItemViewModel.FromInfo(info);
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name)) continue;
            _buffer.Add(item);
        }
    }

    /// <summary>
    /// 重建列表。
    /// 顺序固定为：首行「返回上级」（不在根目录时）→ **全部文件夹** → **全部文件**。
    /// 文件夹与文件**各自成组、组内单独排序**（用户要求：两类同时存在时也要分开排），
    /// 所以按「大小」排序时绝不会把文件排到文件夹前面去。
    /// </summary>
    private void RebuildItems()
    {
        var ordered = Arrange(_buffer, _sortColumn, _sortDescending);

        Items.Clear();

        // 「返回上级」做成列表里的第一行：上一版把它放在工具栏按钮上，
        // 按用户要求移到列表内，像资源管理器里的「..」一样。
        if (_path.Count > 1) Items.Add(N115ItemViewModel.CreateParentEntry());

        foreach (var item in ordered) Items.Add(item);

        // 伪条目不算内容，空目录判断只看真实条目
        IsEmpty = _buffer.Count == 0;

        // 预览的序号是按「显示顺序」算的，重排之后必须跟着重算
        RefreshPreview();
    }

    /// <summary>
    /// 重算每一行的「预览」列（按右侧功能栏当前的替换 / 序号 / 添加删除规则）。
    /// <para>
    /// **改名只作用于选中的条目**，所以：
    /// </para>
    /// <list type="bullet">
    /// <item>**选中的行**：显示按「在选中集合里的位置」编号算出来的新名字（文件夹、文件各自从 0 数）。
    /// 这就是 <see cref="RenameSelectedAsync"/> 实际会用的编号 —— 未选中的条目根本不参与编号。</item>
    /// <item>**未选中的行**：**不显示任何预览**（格子里留空），悬停提示写明「这一项没被选中，本次不会改名」。
    /// 早先给未选中行显示「若整类全选会变成什么」的参考值，结果一调批量序号这些行的预览也跟着变，
    /// 看起来像整张列表都要改名 —— 用户反馈后改成留空。</item>
    /// <item>**一个都没选中**：全部行按「同类全序」显示，都算在范围内（此时改名前必须选中，所以这只是参考视图）。</item>
    /// </list>
    /// 首行「返回上级」是导航伪条目，没有预览。
    /// </summary>
    public void RefreshPreview()
    {
        // 列表还是空的就不必算：打开目录后会由 RebuildItems 刷一次
        if (Items.Count == 0) return;

        // 选中项的序号：文件夹、文件各自从 0 开始数（两者必须分开改名，实际执行时就是这样）
        var scopeIndex = new Dictionary<N115ItemViewModel, int>();
        var selectedFolders = 0;
        var selectedFiles = 0;
        foreach (var item in _selection)
        {
            if (item.IsParentEntry) continue;
            scopeIndex[item] = item.IsDirectory ? selectedFolders++ : selectedFiles++;
        }

        var hasSelection = scopeIndex.Count > 0;

        var folderIndex = 0;
        var fileIndex = 0;

        foreach (var item in Items)
        {
            if (item.IsParentEntry)
            {
                item.ApplyPreview(string.Empty, false, false, true);
                continue;
            }

            // 同类全序里的位置：只有在「一个都没选中」时才用作序号
            var groupIndex = item.IsDirectory ? folderIndex++ : fileIndex++;

            // 有选中、但这一行不在选中集合里 ⇒ 本次不会被改名 ⇒ 预览留空
            if (hasSelection && !scopeIndex.ContainsKey(item))
            {
                item.ApplyPreview(string.Empty, false, false, false);
                continue;
            }

            var index = scopeIndex.TryGetValue(item, out var scopedIndex) ? scopedIndex : groupIndex;

            var (newName, error) = _nameBuilder(item.Name, index, item.IsDirectory, item.GetDateText());

            if (error is not null)
            {
                // 参数不合法（例如「删除个数」填了 0）：把原因直接显示在预览列，
                // 用户一眼就能看出是哪一项设置需要改，而不是对着一堆没反应的预览发呆。
                item.ApplyPreview(error, false, true, true);
                continue;
            }

            item.ApplyPreview(
                newName,
                !string.Equals(newName, item.Name, StringComparison.Ordinal),
                false,
                true);
        }
    }

    /// <summary>
    /// 算出显示顺序：**文件夹整体在前、文件整体在后，两组各自按 <paramref name="column"/> 排序**。
    /// 纯函数，不依赖网络与界面状态，便于离线验证。
    /// </summary>
    public static List<N115ItemViewModel> Arrange(
        IEnumerable<N115ItemViewModel> items, N115SortColumn column, bool descending)
    {
        var folders = new List<N115ItemViewModel>();
        var files = new List<N115ItemViewModel>();

        foreach (var item in items)
        {
            if (item.IsParentEntry) continue;      // 导航用的伪条目由调用方单独插到首行
            (item.IsDirectory ? folders : files).Add(item);
        }

        SortGroup(folders, column, descending);
        SortGroup(files, column, descending);

        folders.AddRange(files);
        return folders;
    }

    /// <summary>
    /// 组内排序。主键相同（例如两个文件夹的「大小」都是空）一律用名称兜底，
    /// 保证顺序稳定、不会每次刷新都乱跳。
    /// </summary>
    private static void SortGroup(List<N115ItemViewModel> group, N115SortColumn column, bool descending)
    {
        group.Sort((a, b) =>
        {
            var primary = column switch
            {
                N115SortColumn.Type => NameComparer.Compare(a.TypeText, b.TypeText),
                N115SortColumn.Size => a.Size.CompareTo(b.Size),
                // 时间按 Unix 秒比，不比格式化后的文本（"2026-10-03 01:47" 与 "2025-09-30 23:59" 比字符串会得到反的结果）
                N115SortColumn.Time => a.UpdateTimeSeconds.CompareTo(b.UpdateTimeSeconds),
                _ => 0,   // 名称列：直接落到下面的名称比较
            };

            return primary != 0 ? primary : NameComparer.Compare(a.Name, b.Name);
        });

        if (descending) group.Reverse();
    }

    /// <summary>
    /// 算出点击某列之后的新排序状态：**首次点某一列给降序**（用户要求「点一次降序、再点升序」），
    /// 点同一列则反转方向。纯函数，便于离线验证。
    /// </summary>
    public static (N115SortColumn Column, bool Descending) NextSort(
        N115SortColumn current, bool currentDescending, N115SortColumn clicked)
        => clicked == current ? (current, !currentDescending) : (clicked, true);

    /// <summary>
    /// 切换排序（由列头点击触发）。返回值表示切换后是否为降序，供界面在列头上画箭头。
    /// </summary>
    public bool ApplySort(N115SortColumn column)
    {
        (_sortColumn, _sortDescending) = NextSort(_sortColumn, _sortDescending, column);

        // 排序会重建 Items，重建过程中 DataGrid 会先清空选中项。
        // 先把选中的对象记下来，重建完按**新的显示顺序**重新选回去 ——
        // 这样批量序号与用户眼前的顺序一致（否则序号会按旧顺序算）。
        // N115ItemViewModel 没有重写 Equals，HashSet 默认就是按对象引用比较。
        var keep = _selection.ToHashSet();

        RebuildItems();

        var restored = Items.Where(keep.Contains).ToList();
        if (restored.Count > 0) SelectionRequested?.Invoke(restored);

        _log($"[115] 排序：{DescribeSort()}；文件夹 {_buffer.Count(x => x.IsDirectory)} 项、"
             + $"文件 {_buffer.Count(x => !x.IsDirectory)} 项分别排序");
        return _sortDescending;
    }

    /// <summary>当前排序的中文描述，用于日志与状态提示。</summary>
    public string DescribeSort() => _sortColumn switch
    {
        N115SortColumn.Type => _sortDescending ? "按类型降序" : "按类型升序",
        N115SortColumn.Size => _sortDescending ? "按大小降序" : "按大小升序",
        N115SortColumn.Time => _sortDescending ? "按修改时间降序" : "按修改时间升序",
        _ => _sortDescending ? "按名称降序" : "按名称升序",
    };

    private string CurrentFolderId => _path.Count > 0 ? _path[^1].Id : RootId;

    private void RebuildCrumbs()
    {
        Crumbs.Clear();
        for (var i = 0; i < _path.Count; i++)
        {
            Crumbs.Add(new N115CrumbViewModel
            {
                Id = _path[i].Id,
                Name = _path[i].Name,
                ShowSeparator = i > 0,
            });
        }
    }

    public void EnterFolder(N115ItemViewModel? folder)
    {
        // 不因 IsBusy 直接忽略：上一个目录可能还在加载，此时双击应取消它并进入新目录，
        // 否则用户会感觉“双击没反应”。
        if (folder is null || !folder.IsDirectory) return;

        // 首行「返回上级」是伪条目：双击它等于回上一级，绝不能当成进入名为「返回上级」的子目录
        if (folder.IsParentEntry)
        {
            GoUp();
            return;
        }

        _path.Add((folder.Id, folder.Name));
        _ = LoadDirectoryAsync(folder.Id);
    }

    /// <summary>返回上一级（工具栏按钮已移除，改由列表首行的「返回上级」触发）。</summary>
    public void GoUp()
    {
        if (_path.Count <= 1) return;

        _path.RemoveAt(_path.Count - 1);
        _ = LoadDirectoryAsync(CurrentFolderId);
    }

    private void GoRoot()
    {
        if (_path.Count <= 1) return;

        _path.RemoveRange(1, _path.Count - 1);
        _ = LoadDirectoryAsync(RootId);
    }

    private void GoToCrumb(N115CrumbViewModel? crumb)
    {
        if (crumb is null) return;

        var index = _path.FindIndex(x => string.Equals(x.Id, crumb.Id, StringComparison.Ordinal));
        if (index < 0 || index == _path.Count - 1) return;

        _path.RemoveRange(index + 1, _path.Count - index - 1);
        _ = LoadDirectoryAsync(crumb.Id);
    }

    // ---------------- 选择 ----------------

    /// <summary>当前选中项（只读视图）。拖动移动用：按下的是选中项 → 整组选中一起拖。</summary>
    public IReadOnlyList<N115ItemViewModel> SelectedItems => _selection;

    /// <summary>由 DataGrid.SelectionChanged 推入当前选中项（支持 Shift / Ctrl 多选）。</summary>
    public void UpdateSelection(System.Collections.IList selectedItems)
    {
        var list = new List<N115ItemViewModel>(selectedItems.Count);
        foreach (var entry in selectedItems)
        {
            // 首行「返回上级」是导航用的伪条目，Ctrl+A 全选时会把它带进来，这里必须滤掉，
            // 否则它会被当成一个可改名的文件夹参与统计。
            if (entry is N115ItemViewModel { IsParentEntry: false } item) list.Add(item);
        }

        _selection = list;

        // Ctrl+A 全选会把首行的「返回上级」也带进来。它被过滤掉了，但界面上那行仍是高亮的，
        // 看起来像「选中了一个不存在的东西」，所以这里直接把选择清掉。
        if (list.Count == 0 && selectedItems.Count > 0)
        {
            SelectionCleared?.Invoke();
        }

        if (list.Count == 0)
        {
            SelectionSummary = "未选择";
        }
        else
        {
            var folders = list.Count(x => x.IsDirectory);
            var files = list.Count - folders;
            SelectionSummary = folders == 0
                ? $"已选 {files} 个文件"
                : files == 0
                    ? $"已选 {folders} 个文件夹"
                    : $"已选 {list.Count} 项（文件 {files} / 文件夹 {folders}，需分开改名）";
        }

        RenameButtonText = BuildRenameButtonText(list);

        // 选中范围变了，预览就要重算：选中行改用「选中序号」，未选中行淡化
        RefreshPreview();

        ClearSelectionCommand.RaiseCanExecuteChanged();
        RenameSelectedCommand.RaiseCanExecuteChanged();
        ToSimplifiedCommand.RaiseCanExecuteChanged();
        RenameSingleCommand.RaiseCanExecuteChanged();
        MoveToCommand.RaiseCanExecuteChanged();
        CopyToCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 「重命名」的可用条件：空闲 + 选中**恰好 1 项**（文件或文件夹均可）。
    /// 2026-10-06 起放开文件夹限制（用户反馈选目录时按钮不可用）：115 的改名接口
    /// fid[] 对文件夹同样接受（批量「按规则重命名」一直在用文件夹 cid），弹窗标题不变。
    /// </summary>
    public bool CanRenameSingle => !IsBusy && _selection.Count == 1;

    /// <summary>
    /// 纯函数：单文件重命名的名称校验。通过返回 null，否则返回给用户看的错误文案。
    /// <para>
    /// 抽成纯函数是为了能离线断言（与 <c>BuildLanguageOnlyOptions</c> / <c>ResolveRenameTargets</c>
    /// 同一套路）—— <see cref="RenameSingleAsync"/> 里只有 <c>MessageBox</c> 没法离线跑。
    /// 也保证 255 字节这条上限只有一处实现，和批量改名完全一致。
    /// </para>
    /// </summary>
    public static string? CheckSingleRenameName(string oldName, string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return "新文件名不能为空。";

        if (string.Equals(newName, oldName, StringComparison.Ordinal))
        {
            return $"新文件名与原名相同（{oldName}），无需重命名。";
        }

        var byteCount = Encoding.UTF8.GetByteCount(newName);
        if (byteCount > MaxNameBytes)
        {
            return $"新文件名是 {byteCount} 字节，超过 115 的 {MaxNameBytes} 字节上限：\n\n  {newName}\n\n请改短一些。";
        }

        return null;
    }

    private static string BuildRenameButtonText(IReadOnlyList<N115ItemViewModel> list)
    {
        if (list.Count == 0) return "按右侧规则重命名";

        var folders = list.Count(x => x.IsDirectory);
        if (folders == 0) return $"重命名选中的 {list.Count} 个文件";
        if (folders == list.Count) return $"重命名选中的 {folders} 个文件夹";
        return "文件/文件夹需分开选择";
    }

    private void ClearSelection()
    {
        _selection = [];
        SelectionSummary = "未选择";
        RenameButtonText = "按右侧规则重命名";

        // 没选中任何条目 ⇒ 预览退回「全员显示」的观感，必须重算
        RefreshPreview();

        SelectionCleared?.Invoke();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        RenameSelectedCommand.RaiseCanExecuteChanged();
        ToSimplifiedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>通知界面清空 DataGrid 的选中项（VM 无法直接改 SelectedItems）。</summary>
    public event Action? SelectionCleared;

    /// <summary>通知界面改选指定的条目（用于「只选文件 / 只选文件夹」快捷选择）。</summary>
    public event Action<IReadOnlyList<N115ItemViewModel>>? SelectionRequested;

    /// <summary>一键选中当前目录里的全部文件或全部文件夹，便于把两类对象分开处理。</summary>
    private void RequestSelect(bool folders)
    {
        // 用 Items（当前排序后的显示顺序）而不是 _buffer，理由有二：
        // ① 首行的「返回上级」是伪条目，不能被选中；
        // ② 批量序号按选择顺序算，取显示顺序才与用户眼前看到的一致。
        var targets = Items.Where(x => !x.IsParentEntry && x.IsDirectory == folders).ToList();
        if (targets.Count == 0)
        {
            StatusText = folders ? "当前目录下没有文件夹" : "当前目录下没有文件";
            return;
        }

        SelectionRequested?.Invoke(targets);
        StatusText = folders ? $"已选中本目录全部 {targets.Count} 个文件夹" : $"已选中本目录全部 {targets.Count} 个文件";
    }

    // ---------------- 批量改名 ----------------

    private Task RenameSelectedAsync() => RenameSelectedCoreAsync(toSimplified: false);

    /// <summary>
    /// 「批量替换」区底部的「替换」按钮（网盘模式）：**有选中改选中，没选中改当前目录全部条目**。
    /// 与预览列的 fallback 语义对齐 —— 预览在未选中时也是按全部条目显示的，
    /// 替换必须同一口径（用户看到预览有结果，点替换就该改那些条目，而不是被要求先选中）。
    /// 全部条目里通常文件 + 文件夹混选，按现有约束拆成两批先后执行（先文件后文件夹）。
    /// </summary>
    public async void ReplaceByRules()
    {
        if (_client is null)
        {
            SetStatusText("尚未登录 115，无法执行替换");
            return;
        }

        if (IsBusy)
        {
            SetStatusText("正在执行其它操作，请等它完成再替换");
            return;
        }

        var selected = _selection.Where(x => !x.IsParentEntry).ToList();
        if (selected.Count > 0)
        {
            // 有选中：与「按右侧规则重命名」完全同一语义
            await RenameSelectedCoreAsync(toSimplified: false);
            return;
        }

        // 没选中：改当前目录全部条目（预览的 fallback 口径）。
        // 文件与文件夹必须分开批：先文件后文件夹，每批走一次 RenameSelectedCoreAsync。
        // 实现 = 临时把全部条目灌进选中集合走老链路（含确认框 / 校验 / 撤销栈 / 状态提示），完成后还原选择。
        var all = Items.Where(x => !x.IsParentEntry).ToList();
        if (all.Count == 0)
        {
            SetStatusText("当前目录是空的，没有可替换的条目");
            return;
        }

        var savedSelection = _selection;
        try
        {
            // 只选文件 → 走一遍；只选文件夹 → 再走一遍（RenameSelectedCoreAsync 按当前 _selection 取目标）
            var files = all.Where(x => !x.IsDirectory).ToList();
            var folders = all.Where(x => x.IsDirectory).ToList();

            if (files.Count > 0)
            {
                _selection = files;
                UpdateSelectionStats();
                await RenameSelectedCoreAsync(toSimplified: false);
            }

            if (folders.Count > 0 && _client is not null && !IsBusy)
            {
                _selection = folders;
                UpdateSelectionStats();
                await RenameSelectedCoreAsync(toSimplified: false);
            }
        }
        finally
        {
            _selection = savedSelection;
            UpdateSelectionStats();
        }
    }

    /// <summary>刷新选中统计（SelectionSummary / RenameButtonText / 命令可用态）。
    /// 供「替换 = 没选中改全部」的临时选中切换后调用。</summary>
    private void UpdateSelectionStats()
    {
        var list = _selection.Where(x => !x.IsParentEntry).ToList();
        var folders = list.Count(x => x.IsDirectory);
        var files = list.Count - folders;
        SelectionSummary = list.Count == 0
            ? "未选择"
            : folders == 0
                ? $"已选 {files} 个文件"
                : files == 0
                    ? $"已选 {folders} 个文件夹"
                    : $"已选 {list.Count} 项（文件 {files} / 文件夹 {folders}，需分开改名）";
        RenameButtonText = BuildRenameButtonText(list);

        ClearSelectionCommand.RaiseCanExecuteChanged();
        RenameSelectedCommand.RaiseCanExecuteChanged();
        ToSimplifiedCommand.RaiseCanExecuteChanged();
        RenameSingleCommand.RaiseCanExecuteChanged();
        MoveToCommand.RaiseCanExecuteChanged();
        CopyToCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 「繁=&gt;简」：与按规则改名共用同一条落地路径（同一套校验、限速、撤销栈），
    /// 差别只在「新名字怎么算」——这里是把原名里的繁体转成简体，不看右侧任何规则。
    /// </summary>
    private Task RenameSelectedToSimplifiedAsync() => RenameSelectedCoreAsync(toSimplified: true);

    private async Task RenameSelectedCoreAsync(bool toSimplified)
    {
        var client = _client;
        if (client is null || IsBusy) return;

        var actionTitle = toSimplified ? "115 网盘 · 繁=>简" : "115 网盘改名";

        if (_selection.Count == 0)
        {
            UiDialog.Show("请先在列表中选中要重命名的条目。\n\n· 单击选中一个\n· Ctrl + 单击多选\n· Shift + 单击连选",
                actionTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folderCount = _selection.Count(x => x.IsDirectory);
        var fileCount = _selection.Count - folderCount;

        // 文件与文件夹必须分开处理，避免同一批里两种对象语义混淆、出事不好回滚。
        if (folderCount > 0 && fileCount > 0)
        {
            UiDialog.Show(
                $"当前选中的 {_selection.Count} 项里同时有 {fileCount} 个文件和 {folderCount} 个文件夹。\n\n"
                + "文件与文件夹不能在同一次操作中改名，请分开进行：\n"
                + "  · 只选文件   → 批量改文件名\n"
                + "  · 只选文件夹 → 批量改文件夹名\n\n"
                + "可点「清除选择」后重新挑选。",
                actionTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 单一类型：要么全是文件，要么全是文件夹
        var renamingFolders = folderCount > 0;
        var kindText = renamingFolders ? "文件夹" : "文件";
        var targets = _selection.ToList();

        var plan = new List<(N115ItemViewModel Item, string NewName)>();
        var unchanged = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            string newName;
            string? error;
            if (toSimplified)
            {
                // 文件名保留扩展名、文件夹整名转换
                newName = ChineseConverter.ToSimplifiedFileName(targets[i].Name, renamingFolders);
                error = null;
            }
            else
            {
                (newName, error) = _nameBuilder(targets[i].Name, i, renamingFolders, targets[i].GetDateText());
            }

            if (error is not null)
            {
                UiDialog.Show(error, "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(newName))
            {
                UiDialog.Show($"「{targets[i].Name}」按当前规则处理后名称为空，请调整规则后重试。",
                    "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (Encoding.UTF8.GetByteCount(newName) > MaxNameBytes)
            {
                UiDialog.Show($"「{targets[i].Name}」的新名称是 {Encoding.UTF8.GetByteCount(newName)} 字节，"
                                + $"超过 115 的 {MaxNameBytes} 字节上限：\n\n  {newName}\n\n请调整规则后重试。",
                    "名称过长", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.Equals(newName, targets[i].Name, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            plan.Add((targets[i], newName));
        }

        if (plan.Count == 0)
        {
            var emptyHint = toSimplified
                ? $"选中的{kindText}名称里没有需要转换的繁体字（已经是简体）。"
                : "按当前规则计算后，选中" + kindText + "的名称没有变化。\n\n请检查右侧「批量替换 / 批量序号 / 添加删除」的设置。";
            UiDialog.Show(emptyHint, actionTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = new StringBuilder();
        foreach (var (item, newName) in plan.Take(12))
        {
            preview.AppendLine($"  {item.Name}  →  {newName}");
        }

        if (plan.Count > 12) preview.AppendLine($"  ...（共 {plan.Count} 项）");

        // 2026-10-06 起不再弹「确认继续？」——改名 / 替换直接执行，完成后弹成功 / 失败数量报告。
        // 改名前先把计划写进日志（执行中的留痕不依赖确认框）。
        _log($"[115] {actionTitle}：对 {plan.Count} 个{kindText}执行改名"
             + (unchanged > 0 ? $"（另有 {unchanged} 个无变化跳过）" : ""));

        var ct = ResetCts();
        IsBusy = true;
        StatusText = $"正在重命名{plan.Count}个{kindText}...";
        _renameRollback.Clear();

        var ok = 0;
        var fail = 0;
        try
        {
            foreach (var (item, newName) in plan)
            {
                var oldName = item.Name;
                try
                {
                    await client.RenameAsync(item.Id, newName, ct);
                    _renameRollback.Add((item.Id, oldName, item));
                    item.Name = newName;
                    ok++;
                    _log($"[115][OK] {oldName} → {newName}");
                    Services.FileLogSink.Debug($"[115] RenameAsync(fid={item.Id}) 返回成功");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    fail++;
                    _log($"[115][FAIL] {oldName} → {newName}：{ex.Message}");
                    Services.FileLogSink.Debug($"[115] RenameAsync(fid={item.Id}) 返回失败：{ex.Message}");
                }

                await Task.Delay(RenameThrottle, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户切换目录或退出，已完成的改名保留
        }
        finally
        {
            IsBusy = false;
        }

        // 名称变了，预览要跟着变（改完的条目再按同一套规则算，通常就显示「不变」了）
        RefreshPreview();
        UndoRenameCommand.RaiseCanExecuteChanged();

        var doneLabel = toSimplified ? "「繁=>简」" : string.Empty;
        StatusText = $"重命名{kindText}完成：成功 {ok}，失败 {fail}";
        _log($"[115] 重命名{kindText}{doneLabel}完成：成功 {ok}，失败 {fail}");
        UiDialog.Show($"115 网盘重命名{kindText}{doneLabel}完成：成功 {ok}，失败 {fail}", actionTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 「重命名」：给**单个文件**改名字 —— 弹窗拿新名字，确认后直接调用改名接口。
    ///
    /// 刻意不走 <see cref="RenameSelectedCoreAsync"/>：那条路是「按右侧规则批量改名」，
    /// 会先算规则、判文件/文件夹分组。这里用户已经手打了完整的新名字，规则一概不参与。
    /// 但**落地方式与撤销栈共用**：成功后照样进 <c>_renameRollback</c>，
    /// 所以改错了点「撤销改名」一样能回退，不会留下一条只能手动改回去的路径。
    /// </summary>
    private async Task RenameSingleAsync()
    {
        var client = _client;
        if (client is null || IsBusy) return;
        if (RenamePrompt is null) return;

        if (_selection.Count != 1)
        {
            UiDialog.Show("「重命名」一次只能改一个条目。\n\n请只选中一个文件或文件夹再试；"
                            + "批量改名请用表格底部的「按右侧规则重命名」。",
                "115 网盘 · 重命名", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var item = _selection[0];

        var oldName = item.Name;
        var input = RenamePrompt(oldName);
        if (input is null) return;   // 用户取消

        // 名称里前后空格是合法字符，不 Trim；校验交给纯函数（与批量改名同一条规则）
        var newName = input;
        var nameError = CheckSingleRenameName(oldName, newName);
        if (nameError is not null)
        {
            UiDialog.Show(nameError, nameError.Contains("字节", StringComparison.Ordinal) ? "名称过长" : "115 网盘 · 重命名",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ct = ResetCts();
        IsBusy = true;
        StatusText = $"正在重命名「{oldName}」...";
        _renameRollback.Clear();

        var ok = 0;
        var fail = 0;
        try
        {
            try
            {
                await client.RenameAsync(item.Id, newName, ct);
                _renameRollback.Add((item.Id, oldName, item));
                item.Name = newName;
                ok++;
                _log($"[115][OK] 重命名 {oldName} → {newName}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                fail++;
                _log($"[115][FAIL] 重命名 {oldName} → {newName}：{ex.Message}");
            }

            await Task.Delay(RenameThrottle, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户切换目录或退出
        }
        finally
        {
            IsBusy = false;
        }

        RefreshPreview();
        UndoRenameCommand.RaiseCanExecuteChanged();
        RenameSingleCommand.RaiseCanExecuteChanged();

        StatusText = fail == 0
            ? $"已重命名：{newName}"
            : $"重命名失败：{oldName}";
        UiDialog.Show(
            ok > 0
                ? $"重命名完成：\n\n  {oldName}  →  {newName}\n\n改错了可以点「撤销改名」回退。"
                : $"重命名失败，名称未改变。\n\n请检查网络或 115 的登录状态后重试。",
            "115 网盘 · 重命名", MessageBoxButton.OK,
            ok > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async Task UndoRenameAsync()
    {
        var client = _client;
        if (client is null || _renameRollback.Count == 0 || IsBusy) return;

        var ct = ResetCts();
        IsBusy = true;
        StatusText = "正在撤销上次改名...";

        var ok = 0;
        var fail = 0;
        try
        {
            foreach (var (fileId, oldName, item) in _renameRollback.AsEnumerable().Reverse())
            {
                try
                {
                    await client.RenameAsync(fileId, oldName, ct);
                    item.Name = oldName;
                    ok++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    fail++;
                    _log($"[115][FAIL] 撤销 {item.Name} → {oldName}：{ex.Message}");
                }

                await Task.Delay(RenameThrottle, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
        }

        _renameRollback.Clear();
        RefreshPreview();
        UndoRenameCommand.RaiseCanExecuteChanged();
        StatusText = $"撤销完成：成功 {ok}，失败 {fail}";
        _log($"[115] 撤销改名：成功 {ok}，失败 {fail}");
        UiDialog.Show($"已撤销上次改名：成功 {ok}，失败 {fail}", "115 网盘", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------------- 移动到 / 复制到 / 删除 ----------------

    /// <summary>
    /// 新建文件夹：弹「新建文件夹命名」窗输入名字，在**当前目录**下创建。
    /// 名称上限与改名同一条规则（255 UTF-8 字节）；成功后**整目录重载** ——
    /// 文件夹置顶 / 排序都要重算，手动往 _buffer 插一行容易把顺序搞乱。
    /// </summary>
    private async Task CreateFolderAsync()
    {
        var client = _client;
        if (client is null || IsBusy) return;
        if (NewFolderPrompt is null) return;

        var name = NewFolderPrompt();
        if (string.IsNullOrWhiteSpace(name)) return;   // 用户取消（空名弹窗里已拦，这里兜底）

        var byteCount = Encoding.UTF8.GetByteCount(name);
        if (byteCount > MaxNameBytes)
        {
            UiDialog.Show($"文件夹名是 {byteCount} 字节，超过 115 的 {MaxNameBytes} 字节上限：\n\n  {name}\n\n请改短一些。",
                "名称过长", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cid = CurrentFolderId;
        var ct = ResetCts();
        IsBusy = true;
        StatusText = $"正在新建文件夹「{name}」...";

        string? error = null;
        try
        {
            await client.CreateFolderAsync(cid, name, ct);
        }
        catch (OperationCanceledException)
        {
            error = "新建文件夹被中断（切换了目录或退出了网盘）";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        if (error is not null)
        {
            StatusText = $"新建文件夹失败：{error}";
            _log($"[115][FAIL] 新建文件夹「{name}」（cid={cid}）：{error}");
            UiDialog.Show($"115 网盘新建文件夹失败：\n\n{error}", "115 网盘 · 新建文件夹",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _log($"[115][OK] 已在「{CurrentPathText}」下新建文件夹「{name}」");
        StatusText = $"已新建文件夹「{name}」";
        Refresh();   // 重载当前目录，新文件夹按「文件夹置顶 + 排序」落到正确位置
    }

    /// <summary>
    /// 拖放移动：把列表里拖动的条目直接移进松手处的目标目录。
    /// 目标可以是列表里的**文件夹行**（移进它），也可以是首行**「返回上级」伪条目**（移到上一级目录）。
    /// 与「移动到」按钮同一条接口通道（POST /files/move，一次请求带全部 fid[0..n]），
    /// 但不弹确认框 —— 拖放本身就是明确的意图，结果用状态行 + 日志反馈；
    /// 服务端拒绝（如移进自己的子目录）时把原文弹给用户。
    /// </summary>
    public async Task MoveByDragAsync(IReadOnlyList<N115ItemViewModel> items, N115ItemViewModel target)
    {
        var client = _client;
        if (client is null || IsBusy) return;

        // 目标解析：真实文件夹行 → 它的 cid；「返回上级」伪条目 → 上一级目录的 cid
        string targetCid;
        string targetName;
        if (target.IsParentEntry)
        {
            if (_path.Count < 2) return;   // 根目录没有「上级」可去
            (targetCid, targetName) = _path[^2];
        }
        else
        {
            if (!target.IsDirectory) return;   // 文件不能作为目标，只能拖进文件夹
            targetCid = target.Id;
            targetName = target.Name;
        }

        // 混进来的伪条目 / 目标本身去掉（整组选中拖动时目标可能也在选中集合里）
        var targets = items
            .Where(x => !x.IsParentEntry && !ReferenceEquals(x, target))
            .Where(x => !(x.IsDirectory && string.Equals(x.Id, targetCid, StringComparison.Ordinal)))
            .ToList();
        if (targets.Count == 0) return;

        var ids = targets.Select(x => x.Id).ToList();
        var ct = ResetCts();
        IsBusy = true;
        StatusText = $"正在移动 {ids.Count} 项到「{targetName}」...";

        string? error = null;
        try
        {
            await client.MoveAsync(ids, targetCid, ct);
        }
        catch (OperationCanceledException)
        {
            error = "移动被中断（切换了目录或退出了网盘）";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        if (error is not null)
        {
            StatusText = $"移动失败：{error}";
            _log($"[115][FAIL] 拖放移动 {ids.Count} 项 → 「{targetName}」(cid={targetCid})：{error}");
            UiDialog.Show($"115 网盘移动失败：\n\n{error}", "115 网盘移动",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var item in targets) _buffer.Remove(item);
        _totalCount = Math.Max(0, _totalCount - targets.Count);
        RebuildItems();
        ClearSelection();
        StatusText = $"已移动 {targets.Count} 项到「{targetName}」";
        _log($"[115][OK] 拖放移动 {targets.Count} 项 → 「{targetName}」(cid={targetCid})");
    }

    /// <summary>把选中项移动到 / 复制到用户挑好的目录。两种动作的流程完全一致，只差接口与结果处理。</summary>
    private async Task TransferAsync(bool move)
    {
        var client = _client;
        if (client is null || IsBusy) return;

        var kind = move ? N115TransferKind.Move : N115TransferKind.Copy;
        var verb = kind.ToVerb();

        var targets = _selection.Where(x => !x.IsParentEntry).ToList();
        if (targets.Count == 0)
        {
            UiDialog.Show("请先在列表中选中要处理的条目。\n\n· 单击选中一个\n· Ctrl + 单击多选\n· Shift + 单击连选",
                $"115 网盘{verb}", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (FolderPicker is null) return;

        // 被移动/复制的文件夹自身不能作为目标（选择器里直接禁止进入，这里再兜一次底）
        var forbidden = targets.Where(x => x.IsDirectory).Select(x => x.Id).ToList();

        var picked = await FolderPicker(client, CurrentPath, kind, forbidden);
        if (picked is null) return;

        if (string.Equals(picked.Id, CurrentFolderId, StringComparison.Ordinal))
        {
            UiDialog.Show(
                $"选中的目标就是当前所在的「{picked.Name}」，内容已经在这里了，不需要再{verb}。\n\n请在选择器里换一层目录。",
                $"115 网盘{verb}", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var conflict = targets.FirstOrDefault(t => t.IsDirectory && picked.PathIds.Contains(t.Id));
        if (conflict is not null)
        {
            UiDialog.Show(
                $"目标目录在「{conflict.Name}」里面，不能把「{conflict.Name}」{verb}到它自己的子目录。",
                $"115 网盘{verb}", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folders = targets.Count(x => x.IsDirectory);
        var files = targets.Count - folders;

        // 2026-10-06 起不再弹「确认继续？」——移动 / 复制直接执行，完成后状态栏 + 日志反馈
        //（失败仍弹窗）。目标与清单在日志里留痕。
        _log($"[115] {verb}：{targets.Count} 项（文件夹 {folders} / 文件 {files}）→ 「{picked.PathText}」(cid={picked.Id})");

        var ids = targets.Select(x => x.Id).ToList();
        var ct = ResetCts();
        IsBusy = true;
        StatusText = $"正在{verb} {ids.Count} 项到「{picked.Name}」...";

        string? error = null;
        try
        {
            // 一次请求带多条 fid[0..n]（已实测 115 支持），不会一项一个请求
            if (move) await client.MoveAsync(ids, picked.Id, ct);
            else await client.CopyAsync(ids, picked.Id, ct);
        }
        catch (OperationCanceledException)
        {
            error = $"{verb}被中断（切换了目录或退出了网盘）";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        if (error is not null)
        {
            StatusText = $"{verb}失败：{error}";
            _log($"[115][FAIL] {verb} {ids.Count} 项 → 「{picked.Name}」(cid={picked.Id})：{error}");
            UiDialog.Show($"115 网盘{verb}失败：\n\n{error}", $"115 网盘{verb}",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (move)
        {
            foreach (var item in targets) _buffer.Remove(item);
            _totalCount = Math.Max(0, _totalCount - targets.Count);
            RebuildItems();
        }

        ClearSelection();
        StatusText = $"已{verb} {targets.Count} 项到「{picked.Name}」";
        _log($"[115][OK] {verb} {targets.Count} 项 → 「{picked.Name}」(cid={picked.Id})");
    }

    /// <summary>删除选中项（进 115 回收站）。文件与文件夹可以混选，因为删除没有「拆扩展名」的语义差异。</summary>
    private async Task DeleteSelectedAsync()
    {
        var client = _client;
        if (client is null || IsBusy) return;

        var targets = _selection.Where(x => !x.IsParentEntry).ToList();
        if (targets.Count == 0)
        {
            UiDialog.Show("请先在列表中选中要删除的条目。", "115 网盘删除",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folders = targets.Count(x => x.IsDirectory);
        var files = targets.Count - folders;

        var summary = new StringBuilder();
        summary.AppendLine($"将从 115 网盘删除 {targets.Count} 项（文件夹 {folders} / 文件 {files}）：");
        summary.AppendLine();
        foreach (var item in targets.Take(12))
        {
            summary.AppendLine($"  {(item.IsDirectory ? "[夹]" : "[文]")} {item.Name}");
        }

        if (targets.Count > 12) summary.AppendLine($"  ...（共 {targets.Count} 项）");
        summary.AppendLine();
        if (folders > 0) summary.AppendLine("文件夹会连同里面的全部内容一起删除。");
        summary.AppendLine("删除后会进入 115 的回收站，可在 115 网页版的「回收站」里恢复。");
        summary.AppendLine();
        summary.Append("确认删除？");

        // 默认按钮放在「否」上，避免习惯性回车误删
        if (UiDialog.Show(summary.ToString(), "115 网盘删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var ids = targets.Select(x => x.Id).ToList();
        var ct = ResetCts();
        IsBusy = true;
        StatusText = $"正在删除 {ids.Count} 项...";

        string? error = null;
        try
        {
            await client.DeleteAsync(ids, CurrentFolderId, ct);
        }
        catch (OperationCanceledException)
        {
            error = "删除被中断（切换了目录或退出了网盘）";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        if (error is not null)
        {
            // 115 的删除是异步的：同一目录上一次删除没跑完会回 990009，服务端原文已经是中文提示，直接照转
            StatusText = $"删除失败：{error}";
            _log($"[115][FAIL] 删除 {ids.Count} 项：{error}");
            UiDialog.Show($"115 网盘删除失败：\n\n{error}", "115 网盘删除",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var item in targets) _buffer.Remove(item);
        _totalCount = Math.Max(0, _totalCount - targets.Count);
        RebuildItems();
        ClearSelection();

        StatusText = $"已删除 {targets.Count} 项（可在 115 回收站恢复）";
        _log($"[115][OK] 删除 {targets.Count} 项（cid={CurrentFolderId}）");
    }

    // ---------------- 错误 ----------------

    private void HandleApiError(N115ApiException ex)
    {
        if (ex.IsAuthError)
        {
            DisposeClient();
            AccountText = "登录已失效";
            StatusText = "登录状态已失效，请点击右上角「重新登录」";
        }
        else
        {
            StatusText = ex.Message;
        }

        _log($"[115] {ex.Message}");
    }

    private void RaiseCommandsCanExecute()
    {
        EnterCommand.RaiseCanExecuteChanged();
        GoUpCommand.RaiseCanExecuteChanged();
        GoRootCommand.RaiseCanExecuteChanged();
        GoToCrumbCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        LoadMoreCommand.RaiseCanExecuteChanged();
        EnterFolderCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        SelectFilesCommand.RaiseCanExecuteChanged();
        SelectFoldersCommand.RaiseCanExecuteChanged();
        RenameSelectedCommand.RaiseCanExecuteChanged();
        ToSimplifiedCommand.RaiseCanExecuteChanged();
        RenameSingleCommand.RaiseCanExecuteChanged();
        MoveToCommand.RaiseCanExecuteChanged();
        CopyToCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        UndoRenameCommand.RaiseCanExecuteChanged();
        ReloginCommand.RaiseCanExecuteChanged();
        LogoutCommand.RaiseCanExecuteChanged();
    }
}
