using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NameTool.Infrastructure;
using NameTool.Services;
using NameTool.ViewModels;
using System.Reflection;

namespace NameTool;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private LogWindow? _logWindow;
    private readonly UpdateService _updateService = new();
    private string? _pendingUpdateFileName;
    private string? _pendingUpdateDownloadUrl;
    private readonly string _displayVersion;
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        var infoVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        _displayVersion = string.IsNullOrWhiteSpace(infoVersion) ? "0.0.0" : infoVersion;
        Title = $"NameTool v{_displayVersion} by Vic4728";
        // 标题栏里的版本号渲染成胶囊（紧凑）；TitleText 只保留程序名，「By Vic4728」在 XAML 里
        VersionPillText.Text = $"v{_displayVersion}";

        Vm.N115.LoginPrompt = ShowN115LoginAsync;
        Vm.N115.FolderPicker = ShowN115FolderPickerAsync;
        Vm.N115.RenamePrompt = ShowN115RenameDialog;
        Vm.N115.SelectionCleared += ClearN115Selection;
        Vm.N115.SelectionRequested += SelectN115Items;
        Vm.N115.SortChanged += SyncN115SortArrows;
        Vm.FilesSortChanged += SyncLocalSortArrows;

        // 有界面 ⇒ 打开交互弹窗（未保存提示 / 只读询问）；离屏校验环境保持默认 false
        Vm.UiPromptsEnabled = true;
        UiDialog.PromptsEnabled = true;   // 统一弹窗门面：同上，离屏环境必须保持静默

        // 编辑模式自动备份：开关 / 间隔 / 当前文件变化时重排定时器
        Vm.PropertyChanged += Vm_PropertyChangedForAutoSave;
        RestartAutoSaveTimer();

        // 列宽记忆：拖完自动存，下次启动照旧。必须在 InitializeComponent 之后 —— 列是 XAML 声明的。
        ColumnWidthManager.Attach(LocalFileGrid, "local");
        ColumnWidthManager.Attach(N115Grid, "n115");

        // 功能区分组的展开 / 收起状态：默认收起，上次展开过的照旧展开。
        // ApplyTemplate 让 RoundedGroupBoxStyle 的 BodyPresenter 立即可查。
        RestoreSectionStates();

        Closing += (sender, args) =>
        {
            // 编辑器里有没保存的改动：先问保存 / 不保存 / 取消，取消就不关窗
            if (!Vm.ConfirmDiscardOrSaveOnExit())
            {
                args.Cancel = true;
                return;
            }

            // 关窗前把还在防抖窗口里的最后一次拖动落盘（不到 800ms 就关窗的情况）
            ColumnWidthManager.FlushAll();
            Vm.SaveSequenceTemplatePresets();
            Vm.N115.Exit();
        };
        Loaded += async (_, _) => await CheckUpdateOnStartupAsync();
    }

    // ---------------- 字幕内容编辑模式 ----------------

    /// <summary>自动备份定时器：间隔 = 用户设置的秒数，到点把未保存改动备份成 原名_bak.扩展名。</summary>
    private DispatcherTimer? _autoSaveTimer;

    /// <summary>开关 / 间隔 / 当前文件变化时重排定时器；关了开关就停。</summary>
    private void Vm_PropertyChangedForAutoSave(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.AutoSaveEnabled)
            or nameof(MainViewModel.AutoSaveSeconds)
            or nameof(MainViewModel.SelectedFile))
        {
            RestartAutoSaveTimer();
        }
    }

    private void RestartAutoSaveTimer()
    {
        _autoSaveTimer?.Stop();
        _autoSaveTimer = null;
        if (!Vm.AutoSaveEnabled) return;

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, Vm.AutoSaveSeconds)) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            if (Vm.SelectedFile is { } item) Vm.WriteAutoBackupNow(item);
        };
        _autoSaveTimer.Start();
    }

    /// <summary>
    /// 「批量替换」区底部的「查找」：按选中规则（没选中就取第一条非空规则）的「查找内容」，
    /// 在编辑器里从当前光标往后找下一个匹配，选中并滚动过去；到结尾绕回开头循环找。
    /// </summary>
    private void FindInEditor_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.CurrentMode != DisplayMode.Editor || Vm.SelectedFile is not { } file)
        {
            Vm.SetEditorStatus("查找只在编辑模式里可用");
            return;
        }

        var find = (Vm.SelectedRule ?? Vm.Rules.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.FindText)))
            ?.FindText?.TrimEnd('\r', '\n') ?? string.Empty;
        if (find.Length == 0)
        {
            Vm.SetEditorStatus("先在「批量替换」的查找里填写要找的内容");
            return;
        }

        var box = SubtitleEditorBox;
        var text = box.Text;
        if (text.Length == 0)
        {
            Vm.SetEditorStatus("编辑内容为空，没有可查找的内容");
            return;
        }

        // 从当前选区末尾往后找；没有就绕回开头（循环查找）
        var start = box.SelectionStart + box.SelectionLength;
        if (start >= text.Length) start = 0;
        var index = text.IndexOf(find, start, StringComparison.Ordinal);
        if (index < 0 && start > 0)
        {
            index = text.IndexOf(find, 0, StringComparison.Ordinal);
        }

        if (index < 0)
        {
            Vm.SetEditorStatus($"没有找到「{find}」");
            return;
        }

        box.Select(index, find.Length);
        box.ScrollToLine(box.GetLineIndexFromCharacterIndex(index));
        box.Focus();

        // 状态栏报第几处：按字符位置之前出现的次数算（与替换语义同为区分大小写的精确匹配）
        var occurrence = 1;
        for (var pos = text.IndexOf(find, StringComparison.Ordinal);
             pos >= 0 && pos < index;
             pos = text.IndexOf(find, pos + find.Length, StringComparison.Ordinal))
        {
            occurrence++;
        }

        Vm.SetEditorStatus($"已选中第 {occurrence} 处「{find}」");
    }

    /// <summary>
    /// Esc 从字幕内容编辑模式回到文件列表。
    /// <para>
    /// 走隧道事件（先于控件处理），但要避开正在展开的下拉框 —— 否则用户按 Esc 想收起
    /// 「预设」下拉框时会连带退出编辑模式。文本框本身不处理 Esc，所以编辑时按它能正常返回。
    /// </para>
    /// </summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        if (Vm.CurrentMode != DisplayMode.Editor) return;
        if (FindAncestor<System.Windows.Controls.ComboBox>(e.OriginalSource as DependencyObject) is not null) return;

        Vm.ReturnToListCommand.Execute(null);
        e.Handled = true;
    }

    // ---------------- 115 网盘 ----------------

    private Task<Services.N115.N115Credential?> ShowN115LoginAsync()
    {
        var dialog = new N115LoginWindow { Owner = this };
        var result = dialog.ShowDialog();
        return Task.FromResult(result == true ? dialog.Credential : null);
    }

    /// <summary>「移动到 / 复制到」的目标文件夹选择器。</summary>
    private Task<N115PickerResult?> ShowN115FolderPickerAsync(
        Services.N115.N115Client client,
        IReadOnlyList<(string Id, string Name)> startPath,
        N115TransferKind kind,
        IReadOnlyList<string> forbiddenIds)
    {
        var dialog = new N115FolderPickerWindow(client, startPath, kind, forbiddenIds) { Owner = this };
        var result = dialog.ShowDialog();
        return Task.FromResult(result == true ? dialog.Result : null);
    }

    /// <summary>
    /// 单文件「重命名」弹窗。入参是当前文件名，返回用户输入的新名字；取消时返回 null。
    /// <para>
    /// 同步返回即可（<c>ShowDialog</c> 本来就是阻塞的），所以这里不返回 Task ——
    /// 由 ViewModel 那边的委托签名决定，保持「弹窗细节全在窗口层」。
    /// </para>
    /// </summary>
    private string? ShowN115RenameDialog(string currentName)
    {
        var dialog = new N115RenameWindow(currentName) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.NewName : null;
    }

    private async void OpenN115_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Vm.N115.EnterAsync();
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, $"打开 115 网盘失败：{ex.Message}", "115 网盘", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExitN115_Click(object sender, RoutedEventArgs e) => Vm.N115.Exit();

    private void ClearN115Selection() => N115Grid.SelectedItems.Clear();

    /// <summary>「只选文件 / 只选文件夹」：由 VM 请求、界面执行改选。</summary>
    private void SelectN115Items(IReadOnlyList<N115ItemViewModel> items)
    {
        N115Grid.SelectedItems.Clear();
        foreach (var item in items)
        {
            N115Grid.SelectedItems.Add(item);
        }

        if (items.Count > 0) N115Grid.ScrollIntoView(items[0]);
    }

    private void N115Grid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => Vm.N115.UpdateSelection(N115Grid.SelectedItems);

    /// <summary>
    /// 表头分隔线右键「重置列宽为默认」。
    /// <para>
    /// 走 <c>PlacementTarget</c> 而不是 sender 的 DataContext：<c>ContextMenu</c> 活在独立的
    /// Popup 可视树里，DataContext 不一定传得进来，而 <c>PlacementTarget</c> 就是挂菜单的那张表，
    /// 一定准。两张表共用同一个处理器。
    /// </para>
    /// </summary>
    private void ResetColumnWidths_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Parent: System.Windows.Controls.ContextMenu menu }) return;
        if (menu.PlacementTarget is not System.Windows.Controls.DataGrid grid) return;

        if (!ColumnWidthManager.ResetToDefault(grid)) return;

        Vm.Log($"已把「{(ReferenceEquals(grid, N115Grid) ? "115 网盘" : "本地文件")}」列表的列宽重置为默认");
    }

    /// <summary>
    /// 本地列表「原文件名」列头点击排序。**故意不交给 DataGrid 自带的排序**：
    /// 自带排序只改视图顺序、不动 Files 集合，而预览里的序号是按集合下标算的，
    /// 结果就是「看着排好了、预览里的编号却是乱的」。这里 Handled=true 拦下来交给 VM，
    /// 由 VM 真排集合并重算预览，规则与 115 列表一致：点一次降序、再点升序。
    /// </summary>
    private void LocalFileGrid_Sorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
    {
        e.Handled = true;

        if (!string.Equals(e.Column.SortMemberPath, nameof(FileItemViewModel.OriginalFileName),
                StringComparison.Ordinal))
        {
            return;
        }

        var descending = Vm.SortFilesByName();

        // 不自己排序就必须手动标箭头，否则用户看不出当前是按哪一列、哪个方向。
        foreach (var column in LocalFileGrid.Columns)
        {
            column.SortDirection = ReferenceEquals(column, e.Column)
                ? (descending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    /// <summary>
    /// 列头点击排序。**故意不交给 DataGrid 自带的排序** —— 它会把「文件夹置顶」
    /// 和首行「返回上级」一起排乱。这里 Handled=true 拦下来，转给 VM 做
    /// 「文件夹 / 文件分组、组内各自排序」，并且点一次降序、再点升序。
    /// </summary>
    private void N115Grid_Sorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
    {
        e.Handled = true;

        // 列上的 SortMemberPath 就是排序键（Name / Type / Size）；
        // 没设的列（「修改时间」已 CanUserSort=False）不动任何顺序。
        if (!Enum.TryParse<N115SortColumn>(e.Column.SortMemberPath, out var column)) return;

        var descending = Vm.N115.ApplySort(column);

        // VM 已经重排完，这里只负责把列头上的箭头标对（DataGrid 不再自己排序，
        // 所以箭头必须手动设，否则用户看不到当前是按哪一列、哪个方向）。
        foreach (var c in N115Grid.Columns)
        {
            c.SortDirection = ReferenceEquals(c, e.Column)
                ? (descending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    /// <summary>
    /// VM 重置本地排序（拖入 / 添加文件 → 默认按原文件名升序）后，把列头箭头同步成当前状态。
    /// </summary>
    private void SyncLocalSortArrows()
    {
        foreach (var column in LocalFileGrid.Columns)
        {
            var isNameColumn = string.Equals(column.SortMemberPath, nameof(FileItemViewModel.OriginalFileName),
                StringComparison.Ordinal);
            column.SortDirection = isNameColumn && Vm.IsFilesSortedByName
                ? (Vm.IsFilesSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    /// <summary>
    /// VM 重置 115 排序（打开目录 → 默认按名称升序）后，把列头箭头同步成当前状态。
    /// </summary>
    private void SyncN115SortArrows()
    {
        foreach (var c in N115Grid.Columns)
        {
            var isSortColumn = Enum.TryParse<N115SortColumn>(c.SortMemberPath, out var col)
                && col == Vm.N115.CurrentSortColumn;
            c.SortDirection = isSortColumn
                ? (Vm.N115.IsSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    /// <summary>
    /// 该行是不是「返回上级」伪条目。单击 / 双击两条路径共用的判定，
    /// 抽成纯函数以便离线校验（val 直接 new DataGridRow 断言，不需要真窗口）。
    /// </summary>
    public static bool IsParentEntryRow(System.Windows.Controls.DataGridRow? row) =>
        row?.Item is N115ItemViewModel { IsParentEntry: true };

    /// <summary>上次单击「返回上级」行的时间（UTC），用来挡掉凑成双击的第二击。</summary>
    private DateTime _lastN115ParentNavUtc = DateTime.MinValue;

    /// <summary>
    /// 「返回上级」行：**单击**即回上一级（原先要双击，用户反馈不直觉）。
    /// <para>
    /// 挂隧道事件先于 DataGrid 的行选择跑：命中伪条目就导航，然后把点击吞掉 ——
    /// 别把伪条目行选中（UpdateSelection 会把它过滤掉，选中毫无意义）。
    /// 400ms 内的重复点击只算一次：单击触发导航后，紧跟的第二击（凑成双击，
    /// 此时旧列表往往还没刷新完，第二击仍会命中「返回上级」行）绝不能再跳一级，否则连退两级。
    /// </para>
    /// </summary>
    private void N115Grid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!IsParentEntryRow(FindAncestor<System.Windows.Controls.DataGridRow>(e.OriginalSource as DependencyObject)))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastN115ParentNavUtc).TotalMilliseconds >= 400)
        {
            _lastN115ParentNavUtc = now;
            Vm.N115.GoUp();
        }
        e.Handled = true;
    }

    // ---------------- 批量序号 ----------------

    /// <summary>「批量序号」区右上角的「变量说明」：弹出变量表，变量名可选中 / 一键复制。</summary>
    private void SequenceVariables_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SequenceVariablesWindow { Owner = this };
        dlg.ShowDialog();
    }

    private void N115Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var row = FindAncestor<System.Windows.Controls.DataGridRow>(e.OriginalSource as DependencyObject);

        // 「返回上级」行：单击路径已经处理过导航，这里直接忽略 —— 不拦的话一次双击会连跳两级
        if (IsParentEntryRow(row)) return;

        if (row?.Item is N115ItemViewModel { IsDirectory: true } folder)
        {
            Vm.N115.EnterFolder(folder);
        }
    }

    /// <summary>
    /// 「参与」列的复选框：点它时**不要**让 DataGrid 顺带选中该行 ——
    /// 勾个选不该改变行选中状态（多选场景下还会丢掉已有的选中集合）。
    /// 这里把落在复选框上的点击拦下（隧道事件，先于 DataGrid 的行选择逻辑），并手动翻转勾选。
    /// </summary>
    private void LocalFileGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.CheckBox>(e.OriginalSource as DependencyObject) is not { } checkBox)
        {
            // 记录按下位置：按住左键移出阈值 → 进入「拖动行调整排序」
            _reorderDragRow = FindAncestor<System.Windows.Controls.DataGridRow>(e.OriginalSource as DependencyObject);
            _reorderDragStart = e.GetPosition(LocalFileGrid);
            return;
        }

        // 从复选框起手不触发拖动排序（复选框只负责勾选）
        _reorderDragRow = null;
        checkBox.IsChecked = checkBox.IsChecked != true;
        e.Handled = true;
    }

    /// <summary>
    /// 双击行进入编辑模式（2026-10-05 起**单选不再自动进编辑器**，双击 / 点状态列编辑图标才是入口）。
    /// 二进制文件没有可编辑文本，OpenInEditor 里会静默拒绝并提示。
    /// </summary>
    private void LocalFileGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LocalFileGrid.SelectedItem is FileItemViewModel item)
        {
            Vm.OpenInEditor(item);
        }
    }

    /// <summary>状态列里的编辑图标：点击进入编辑模式（与双击行等效）。</summary>
    private void EditIcon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.DataContext is FileItemViewModel item)
        {
            Vm.OpenInEditor(item);
        }
    }

    // ---------------- 文件列表：按住左键拖动行调整排序 ----------------

    /// <summary>内部拖动排序用的自定义 DataObject 格式（不会与外部文件拖入的 FileDrop 混淆）。</summary>
    private const string RowReorderFormat = "NameTool.RowReorder";

    /// <summary>按下时所在的行（null = 按在复选框 / 表头 / 滚动条上，不起拖）。</summary>
    private System.Windows.Controls.DataGridRow? _reorderDragRow;

    /// <summary>按下时的位置（判断是否移出系统拖动阈值）。</summary>
    private Point _reorderDragStart;

    /// <summary>按住左键移动：移出拖动阈值且按在选中行上 → 起拖（整组选中项一起排序）。</summary>
    private void LocalFileGrid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            _reorderDragRow = null;
            return;
        }

        if (_reorderDragRow is null)
        {
            return;
        }

        var pos = e.GetPosition(LocalFileGrid);
        if (Math.Abs(pos.X - _reorderDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _reorderDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var row = _reorderDragRow;
        _reorderDragRow = null;   // 一次按压只起拖一次
        if (row.Item is not FileItemViewModel dragItem) return;
        if (!LocalFileGrid.SelectedItems.Contains(dragItem)) return;

        var data = new System.Windows.DataObject(RowReorderFormat, dragItem);
        System.Windows.DragDrop.DoDragDrop(row, data, System.Windows.DragDropEffects.Move);
    }

    /// <summary>内部排序拖动悬停：标记为可放置（Move），并在目标位置画橙色插入指示线。
    /// 外部文件拖入走 FileDrop，不进这里。</summary>
    private void LocalFileGrid_ReorderDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(RowReorderFormat)) return;

        var info = ComputeReorderInsertInfo(e);
        UpdateReorderIndicator(info);
        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>内部排序拖动离开列表：收起插入指示线（拖到列表外面去了）。</summary>
    private void LocalFileGrid_ReorderDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        // 在 DataGrid 内部子元素之间移动时也会触发 DragLeave，用坐标二次确认，避免指示线闪烁
        var p = e.GetPosition(LocalFileGrid);
        if (p.X < 0 || p.Y < 0 || p.X > LocalFileGrid.ActualWidth || p.Y > LocalFileGrid.ActualHeight)
        {
            UpdateReorderIndicator(null);
        }
    }

    /// <summary>
    /// 计算排序拖动的插入位置：**纯布局空间计算（扣除行让位位移），不依赖命中测试**。
    /// 让位动效会把两行撑开一条缝，按命中测试时鼠标进了缝会找不到行 → 被误判为
    /// 「空白处」→ 插入点跳到列表末尾再弹回来，指示线与行位移来回震荡（抖动根因）。
    /// 落到目标行上半 = 插到它前面，下半 = 插到它后面，行带之外（表头 / 行下方空区）= 移到末尾。
    /// DragOver 的指示线与 Drop 的落点**共用这一个算法**，保证所见即所得。
    /// </summary>
    private ReorderInsertInfo? ComputeReorderInsertInfo(System.Windows.DragEventArgs e)
    {
        var gridPos = e.GetPosition(LocalFileGrid);

        for (int i = 0; i < Vm.Files.Count; i++)
        {
            if (LocalFileGrid.ItemContainerGenerator.ContainerFromIndex(i) is not System.Windows.Controls.DataGridRow row
                || !row.IsLoaded)
            {
                continue;
            }

            // 视觉位置 - 让位位移 = 布局位置（让位位移全部由本类挂的 TranslateTransform 施加）
            double top = row.TransformToVisual(LocalFileGrid).Transform(new Point(0, 0)).Y - CurrentShiftY(row);
            if (gridPos.Y < top || gridPos.Y >= top + row.ActualHeight)
            {
                continue;
            }

            bool above = gridPos.Y < top + row.ActualHeight / 2;
            return new ReorderInsertInfo(
                row,
                above ? i : i + 1,
                above ? top : top + row.ActualHeight);
        }

        // 行带之外（表头 / 行下方空白区）：插入到列表末尾，线画在最后一行的布局下缘
        if (Vm.Files.Count == 0) return null;
        var lastRow = FindRowForItem(Vm.Files[^1]);
        if (lastRow is null) return null;
        double lastTop = lastRow.TransformToVisual(LocalFileGrid).Transform(new Point(0, 0)).Y - CurrentShiftY(lastRow);
        return new ReorderInsertInfo(null, Vm.Files.Count, lastTop + lastRow.ActualHeight);
    }

    /// <summary>读取某行当前让位位移的 Y 值（动画进行中返回当前帧值；没挂位移返回 0）。</summary>
    private double CurrentShiftY(System.Windows.Controls.DataGridRow row)
        => _shiftedRows.TryGetValue(row, out var tt) ? tt.Y : 0;

    /// <summary>在 Items 里找到某个文件对应的行容器（可能未虚拟化出来，找不到返回 null）。</summary>
    private System.Windows.Controls.DataGridRow? FindRowForItem(FileItemViewModel? item)
    {
        if (item is null) return null;
        return LocalFileGrid.ItemContainerGenerator.ContainerFromItem(item) as System.Windows.Controls.DataGridRow;
    }

    /// <summary>更新橙色插入指示线 + 行让位动效；insertInfo 为 null 时一起收起。</summary>
    private void UpdateReorderIndicator(ReorderInsertInfo? insertInfo)
    {
        if (_reorderIndicatorAdorner is null)
        {
            var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(LocalFileGrid);
            if (layer is null) return;
            _reorderIndicatorAdorner = new LineInsertIndicatorAdorner(LocalFileGrid);
            layer.Add(_reorderIndicatorAdorner);
        }

        _reorderIndicatorAdorner.Update(insertInfo);
        ApplyRowShift(insertInfo);
    }

    /// <summary>让位位移量（px）：插入点上一行上移、下一行下移，让出一条缝给指示线。</summary>
    private const double ReorderRowShift = 5.0;

    /// <summary>当前处于让位位移状态的行 → 挂上去的 TranslateTransform（全部由本类创建）。</summary>
    private readonly System.Collections.Generic.Dictionary<System.Windows.Controls.DataGridRow, System.Windows.Media.TranslateTransform>
        _shiftedRows = new();

    /// <summary>
    /// 行让位动效：插入点上一行轻微上移、下一行轻微下移（1/2/3/4/5 把 4 拖到 1、2 之间
    /// ⇒ 文件 1 上移、文件 2 下移）。用 RenderTransform + 短动画实现，不参与布局；
    /// 与指示线同一数据源（InsertIndex），DragLeave / Drop 置 null 时一起复位。
    /// </summary>
    private void ApplyRowShift(ReorderInsertInfo? info)
    {
        // 分界线画在 Files[InsertIndex-1] 与 Files[InsertIndex] 之间：上一行上移、下一行下移
        var upRow = info is null ? null
            : LocalFileGrid.ItemContainerGenerator.ContainerFromIndex(info.InsertIndex - 1) as System.Windows.Controls.DataGridRow;
        var downRow = info is null ? null
            : LocalFileGrid.ItemContainerGenerator.ContainerFromIndex(info.InsertIndex) as System.Windows.Controls.DataGridRow;

        var keep = new HashSet<System.Windows.Controls.DataGridRow>();
        if (upRow is not null) keep.Add(upRow);
        if (downRow is not null) keep.Add(downRow);

        // 先复位已不在让位集合里的行（插入点移走 / 拖动结束）
        foreach (var row in _shiftedRows.Keys.ToList())
        {
            if (keep.Contains(row)) continue;
            AnimateRowShiftTo(row, 0);
            _shiftedRows.Remove(row);
        }

        if (upRow is not null) AnimateRowShiftTo(upRow, -ReorderRowShift);
        if (downRow is not null) AnimateRowShiftTo(downRow, ReorderRowShift);
    }

    /// <summary>把某行的让位位移动画到 targetY（TransformTransform 由本方法按需创建）。</summary>
    private void AnimateRowShiftTo(System.Windows.Controls.DataGridRow row, double targetY)
    {
        if (!_shiftedRows.TryGetValue(row, out var tt))
        {
            tt = new System.Windows.Media.TranslateTransform();
            row.RenderTransform = tt;
            _shiftedRows[row] = tt;
        }

        if (Math.Abs(tt.Y - targetY) < 0.01) return;   // 已在目标位置，不重复起动画

        var anim = new System.Windows.Media.Animation.DoubleAnimation(targetY, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        tt.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, anim);
    }

    /// <summary>插入指示线装饰器实例（首次拖动时创建，之后复用）。</summary>
    private LineInsertIndicatorAdorner? _reorderIndicatorAdorner;

    /// <summary>插入位置描述：InsertIndex = 插入下标；LineY = 指示线在 DataGrid
    /// **布局坐标系**里的 Y 值（已扣除让位位移，不随行动画漂移）；
    /// Row = 插入点所在的基准行（空白处插到末尾时为 null，用于 Drop 的「落自己组内」判断）。</summary>
    private sealed record ReorderInsertInfo(
        System.Windows.Controls.DataGridRow? Row,
        int InsertIndex,
        double LineY);

    /// <summary>
    /// 橙色插入指示线装饰器：画在 DataGrid 的装饰层上，不参与布局、不挡鼠标。
    /// </summary>
    private sealed class LineInsertIndicatorAdorner : System.Windows.Documents.Adorner
    {
        private const double LineThickness = 3.0;
        private const double HorizontalInset = 4.0;
        private static readonly System.Windows.Media.Pen LinePen = CreatePen();

        private ReorderInsertInfo? _insertInfo;

        public LineInsertIndicatorAdorner(System.Windows.Controls.DataGrid grid) : base(grid)
        {
            IsHitTestVisible = false;
        }

        /// <summary>更新指示线位置；为 null 时隐藏（不画）。</summary>
        public void Update(ReorderInsertInfo? insertInfo)
        {
            _insertInfo = insertInfo;
            Visibility = insertInfo is null ? Visibility.Hidden : Visibility.Visible;
            InvalidateVisual();
        }

        private static System.Windows.Media.Pen CreatePen()
        {
            var pen = new System.Windows.Media.Pen(
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0x82, 0x0C)),
                LineThickness);
            pen.Freeze();
            return pen;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_insertInfo is null) return;

            var adorned = (FrameworkElement)AdornedElement;

            // LineY 已是布局坐标（计算时扣除了让位位移），直接定位，不随行动画漂移
            double y = _insertInfo.LineY;
            double left = HorizontalInset;
            double right = Math.Max(left + 1, adorned.ActualWidth - HorizontalInset);

            drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, adorned.ActualWidth, adorned.ActualHeight)));

            // 主线（横贯整行宽）
            drawingContext.DrawLine(LinePen, new Point(left, y), new Point(right, y));

            drawingContext.Pop();
        }
    }

    /// <summary>内部排序拖动落下：落到目标行上半 = 插到它前面，下半 = 插到它后面，
    /// 落到列表空白处 = 移到末尾；**整组选中项一起移动**，组内相对顺序不变。
    /// 落点算法与 DragOver 的指示线完全一致 —— 线画在哪，松手就插在哪。</summary>
    private void LocalFileGrid_ReorderDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(RowReorderFormat)) return;
        e.Handled = true;

        if (e.Data.GetData(RowReorderFormat) is not FileItemViewModel dragItem) return;
        var dragged = LocalFileGrid.SelectedItems.OfType<FileItemViewModel>().ToList();
        if (dragged.Count == 0) dragged = [dragItem];

        var draggedSet = new HashSet<FileItemViewModel>(dragged);
        var info = ComputeReorderInsertInfo(e);
        UpdateReorderIndicator(null);   // 无论落不落得下，先把指示线收掉

        if (info is null) return;

        // 与旧行为一致：落点在自己组内的行上 → 不动（空白处 Row=null，无此限制）
        if (info.Row is not null && draggedSet.Contains((FileItemViewModel)info.Row.Item)) return;

        Vm.MoveFilesTo(dragged, info.InsertIndex);
    }

    /// <summary>Delete 键移除选中的文件（只移出列表，不删文件本体）。单元格只读，不会和编辑冲突。</summary>
    private void LocalFileGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Delete or System.Windows.Input.Key.Back))
        {
            return;
        }

        // 焦点在 DataGrid 内部的 TextBox（目前没有）时不劫持；焦点在外面的编辑框（编辑器等）不归这张表管
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox)
        {
            return;
        }


        RemoveSelectedLocalFiles();
        e.Handled = true;
    }

    /// <summary>「移除」按钮：把 DataGrid 选中项（支持 Ctrl / Shift 多选）移出列表，不删文件本体。</summary>
    private void RemoveFiles_Click(object sender, RoutedEventArgs e) => RemoveSelectedLocalFiles();

    private void RemoveSelectedLocalFiles() => Vm.RemoveFiles(LocalFileGrid.SelectedItems);

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private async Task CheckUpdateOnStartupAsync()
    {
        try
        {
            var result = await _updateService.CheckForUpdateAsync();
            if (result.Status != UpdateStatus.UpdateAvailable || string.IsNullOrWhiteSpace(result.FileName))
            {
                UpdateNoticeText.Visibility = Visibility.Collapsed;
                _pendingUpdateFileName = null;
                _pendingUpdateDownloadUrl = null;
                return;
            }

            _pendingUpdateFileName = result.FileName;
            _pendingUpdateDownloadUrl = result.DownloadUrl;
            UpdateNoticeText.Visibility = Visibility.Visible;
        }
        catch
        {
            UpdateNoticeText.Visibility = Visibility.Collapsed;
        }
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            // 任意文件都能进列表（字幕 / 文本可改内容，其余仅改名），所以默认档就是「所有文件」
            Filter = "所有文件|*.*|字幕 / 文本（可编辑内容）|*.srt;*.ass;*.txt|音视频|*.mp3;*.mp4;*.mkv;*.mov;*.ts;*.wav"
        };
        if (dlg.ShowDialog(this) == true)
        {
            AddPathsWithFeedback(dlg.FileNames);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            AddPathsWithFeedback([dlg.FolderName]);
        }
    }

    /// <summary>
    /// 添加文件/文件夹（按钮、拖拽共用），并在“一个也没加进去”时给出明确提示，
    /// 避免用户以为操作失效。
    /// </summary>
    private void AddPathsWithFeedback(IReadOnlyList<string> paths)
    {
        try
        {
            var added = Vm.AddPaths(paths);
            if (added == 0)
            {
                UiDialog.Show(this,
                    "未添加任何文件。可能原因：\n"
                    + "- 文件不存在或已被删除\n"
                    + "- 这些文件已经在列表中（已自动去重）\n"
                    + "- 文件夹内没有可读取的文件（可勾选“递归扫描文件夹”后重试）\n"
                    + "- 文件夹或子目录无访问权限（详见日志）",
                    "添加文件",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, $"添加文件失败：{ex.Message}", "添加文件", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 字幕编辑工具条上的「另存为」：弹保存对话框，把编辑器内容写到选中的文件，原文件不动。
    /// （「保存」不需要对话框，直接走 ViewModel 的命令。）
    /// </summary>
    private void SaveSubtitleAs_Click(object sender, RoutedEventArgs e)
    {
        var file = Vm.SelectedFile;
        if (file is null || !file.IsTextFile)
        {
            return;
        }

        var ext = System.IO.Path.GetExtension(file.FilePath);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存为",
            FileName = file.OriginalFileName,
            DefaultExt = ext,
            Filter = "字幕 / 文本|*.srt;*.ass;*.txt|所有文件|*.*",
            OverwritePrompt = true
        };

        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            Vm.SaveSelectedFileAs(dlg.FileName);
        }
    }

    // ---------------- 字幕编辑器：行号列与滚动同步 ----------------

    /// <summary>编辑框内部的 ScrollViewer（模板应用后挖一次缓存住；null 表示还没就绪）。</summary>
    private ScrollViewer? _subtitleEditorScroll;

    /// <summary>编辑器内容变化：行数变了才重建行号文本（避免每个按键都整列重排、闪烁）。</summary>
    private void SubtitleEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        // TextChanged 时布局还没跑完，LineCount 可能滞后 —— 排完再来取
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateSubtitleLineNumbers);
    }

    /// <summary>编辑器模板就绪：接滚动同步、挖内部 ScrollViewer。</summary>
    private void SubtitleEditor_Loaded(object sender, RoutedEventArgs e)
    {
        // 双保险：查找高亮 = 选区黄底 + **黑字**（XAML 已设 SelectionTextBrush，
        // 这里再赋一次，防止个别系统主题/模板把选中文字画回白色看不清）
        SubtitleEditorBox.SelectionTextBrush = System.Windows.Media.Brushes.Black;

        EnsureEditorScrollSync();

        UpdateSubtitleLineNumbers();
    }

    /// <summary>接上「编辑器滚动 → 行号列跟滚」的同步。
    /// ⚠️ 编辑器在窗口启动时整块 Collapsed，Loaded 只触发一次且那时内部模板还没应用，
    /// FindScrollViewer 挖不到内部 ScrollViewer，_subtitleEditorScroll 永远是 null ⇒
    /// 行号列不跟手（5520 行的文件拉到底仍停在开头一屏），改一个字触发重建才被
    /// UpdateSubtitleLineNumbers 末尾的 ScrollToVerticalOffset「顺带修好」。
    /// 修法：进入编辑模式（可见 → 布局完成）后再挖一次，此时模板必然已应用。</summary>
    private void EnsureEditorScrollSync()
    {
        if (_subtitleEditorScroll is null)
        {
            _subtitleEditorScroll = FindScrollViewer(SubtitleEditorBox);
            if (_subtitleEditorScroll is not null)
            {
                _subtitleEditorScroll.ScrollChanged += (_, ev) =>
                    SubtitleLineNumbersBox.ScrollToVerticalOffset(ev.VerticalOffset);
            }
        }
    }

    /// <summary>行号列当前已生成的行数（-1 = 还没生成过）。
    /// 自己记数而不比对两个框的 LineCount —— 首次进编辑模式时两个框都还没参与布局，
    /// LineCount 是「未布局」的假值，比对会误判成「行数没变」而整列不刷新。</summary>
    private int _lineNumberCount = -1;

    /// <summary>按编辑框行数重建行号列（1..N，右对齐），并跟住当前滚动位置。
    /// 列宽按**最大行号的实际位数**自适应（2026-10-05 用户要求调小：只量数字本身，
    /// 右侧留 3px 呼吸位，不再前后各垫一个空格）。
    /// ⚠️ 行数**直接数文本里的换行符**，不读 editor.LineCount：编辑区还没参与布局时
    /// LineCount 返回假值（首进编辑模式行号列空白的根因），数换行符与布局无关，任何时机都准。</summary>
    private void UpdateSubtitleLineNumbers()
    {
        var editor = SubtitleEditorBox;
        if (editor is null) return;

        var text = editor.Text ?? string.Empty;
        var count = 1;
        foreach (var c in text)
        {
            if (c == '\n') count++;
        }

        if (_lineNumberCount != count)
        {
            _lineNumberCount = count;
            var sb = new System.Text.StringBuilder((count + 1) * 7);
            for (var i = 1; i <= count; i++)
            {
                sb.Append(i).Append('\n');
            }

            SubtitleLineNumbersBox.Text = sb.ToString();
            SubtitleLineNumbersBox.Width = MeasureLineNumberWidth(count);
        }

        // 重建文本会重置行号框的滚动位置 —— 立刻跟回编辑框当前偏移
        SubtitleLineNumbersBox.ScrollToVerticalOffset(_subtitleEditorScroll?.VerticalOffset ?? editor.VerticalOffset);

        UpdateEditorStats(text, count);
    }

    /// <summary>编辑器底部状态栏：总行数 / 总字数（不含换行符）。
    /// 大小与编码直接绑 SelectedFile 的属性，保存 / 编码转换后由属性通知自动刷新，不在这里管。</summary>
    private void UpdateEditorStats(string text, int lineCount)
    {
        if (EditorStatLines is null) return;

        var chars = 0;
        foreach (var c in text)
        {
            if (c != '\r' && c != '\n') chars++;
        }

        EditorStatLines.Text = lineCount.ToString("N0");
        EditorStatChars.Text = chars.ToString("N0");
    }

    /// <summary>编辑区从隐藏翻到可见后，布局才真正跑一遍 —— 排完再刷行号列与统计。
    /// 首次进入编辑模式时内容绑定发生在可见之前，那时算的行号 / 统计都是「未布局」的假值，
    /// 事后又没有别的事件会再触发，所以必须在这里补一刀。</summary>
    private void EditorGrid_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                EnsureEditorScrollSync();   // 此时布局已跑、模板已应用 —— 滚动同步在这才真正接得上
                UpdateSubtitleLineNumbers();
            });
        }
    }

    /// <summary>
    /// 量出 N 位行号所需的像素宽（Consolas 等宽字体，直接量最长那行；只量数字本身）。
    /// 右侧补 3px 呼吸位，防止四舍五入后末位贴边；Hidden 状态的竖滚动条不占宽，不用补。
    /// </summary>
    private double MeasureLineNumberWidth(int maxLineNumber)
    {
        var digits = Math.Max(1, maxLineNumber.ToString().Length);
        var sample = new string('8', digits);   // 8 最宽，量出来最保险

        var typeface = new Typeface(
            SubtitleLineNumbersBox.FontFamily,
            SubtitleLineNumbersBox.FontStyle,
            SubtitleLineNumbersBox.FontWeight,
            SubtitleLineNumbersBox.FontStretch);

        var ft = new FormattedText(
            sample,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            SubtitleLineNumbersBox.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        return Math.Ceiling(ft.Width) + 3;
    }

    /// <summary>沿可视树往下找第一个 ScrollViewer（TextBox 的滚动条藏在模板里，没有公开属性）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }

        return null;
    }

    // ---------------- 功能区分组：展开 / 收起（状态记忆） ----------------

    /// <summary>启动时恢复三个可折叠分组的展开状态（默认收起；<c>ui-state.json</c> 记了什么就用什么）。
    /// 走**逻辑树**：构造阶段窗口还没 Show、可视树没建，逻辑树这时才找得到分组头。</summary>
    private void RestoreSectionStates()
    {
        foreach (var border in FindSectionHeaderBorders(this))
        {
            if (border.Tag is not string key) continue;
            if (LogicalTreeHelper.GetParent(border) is not GroupBox owner) continue;

            SetSectionExpanded(owner, UiStateStore.GetBool("section." + key, fallback: false));
        }
    }

    /// <summary>点分组头（整条标题栏都可点）：切换该分组正文的展开 / 收起，并立刻落盘记忆。</summary>
    private void SectionHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string key } border) return;
        if (LogicalTreeHelper.GetParent(border) is not GroupBox owner) return;

        SetSectionExpanded(owner, !IsSectionExpanded(owner));
        UiStateStore.SetBool("section." + key, IsSectionExpanded(owner));
    }

    /// <summary>正文是否展开（BodyPresenter 没被 Collapsed 就算展开）。</summary>
    private static bool IsSectionExpanded(GroupBox section)
        => GetSectionBody(section)?.Visibility != Visibility.Collapsed;

    /// <summary>切换分组正文的可见性，并同步头上的折叠箭头（▸ 收起 / ▾ 展开）。</summary>
    private static void SetSectionExpanded(GroupBox section, bool expanded)
    {
        var body = GetSectionBody(section);
        if (body is null) return;

        body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        if (section.Header is DependencyObject headerRoot && FindChevron(headerRoot) is { } chevron)
        {
            chevron.Text = expanded ? "▾" : "▸";
        }
    }

    /// <summary>模板里命了名的正文 ContentPresenter（收起时整块隐藏，连带 Margin 一起消失，不留白缝）。
    /// 模板可能还没应用（启动阶段 / 未 Show 的窗口），这里顺手 ApplyTemplate。</summary>
    private static ContentPresenter? GetSectionBody(GroupBox section)
    {
        if (!section.IsLoaded)
        {
            try { section.ApplyTemplate(); }
            catch { /* 应用不了就当没有，保持原样 */ }
        }

        return section.Template?.FindName("BodyPresenter", section) as ContentPresenter;
    }

    /// <summary>折叠箭头：Header 元素树里 Tag=chevron 的 TextBlock。逻辑树优先（未 Show 时也有），
    /// 找不到再走可视树（Header 被 ContentPresenter 重排过的情况）。</summary>
    private static TextBlock? FindChevron(DependencyObject root)
    {
        if (root is TextBlock tb && ReferenceEquals(tb.Tag, "chevron")) return tb;

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            var found = FindChevron(d);
            if (found is not null) return found;
        }

        if (root is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindChevron(VisualTreeHelper.GetChild(root, i));
                if (found is not null) return found;
            }
        }

        return null;
    }

    /// <summary>窗口里所有可折叠分组头（Border 且 Tag 是以字母/数字开头的记忆键 —— 区别于没 Tag 的普通 Border）。
    /// 同样走逻辑树，启动阶段就能收集齐。</summary>
    private static List<Border> FindSectionHeaderBorders(DependencyObject root)
    {
        var result = new List<Border>();
        CollectSectionHeaderBorders(root, result);
        return result;
    }

    private static void CollectSectionHeaderBorders(DependencyObject root, List<Border> result)
    {
        if (root is Border { Tag: string tag } border && tag.Length > 0 && char.IsAsciiLetterOrDigit(tag[0]))
        {
            result.Add(border);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            CollectSectionHeaderBorders(d, result);
        }

        if (root is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                CollectSectionHeaderBorders(VisualTreeHelper.GetChild(root, i), result);
            }
        }
    }

    /// <summary>
    /// 「批量替换」区右上角的「说明」：样式与「批量序号 · 变量说明」看齐的独立弹窗
    /// （通配符 + 查找替换·变量，均可选中 / 一键复制）。
    /// </summary>
    private void ShowReplaceHelp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ReplaceVariablesWindow
        {
            Owner = this
        };
        dlg.ShowDialog();
    }

    private void ShowLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow is null || !_logWindow.IsLoaded)
        {
            _logWindow = new LogWindow
            {
                Owner = this,
                DataContext = DataContext
            };
            _logWindow.Closed += (_, _) => _logWindow = null;
            _logWindow.Show();
            return;
        }

        if (_logWindow.WindowState == WindowState.Minimized)
        {
            _logWindow.WindowState = WindowState.Normal;
        }

        _logWindow.Activate();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as System.Windows.Controls.Button;
        if (button is not null) button.IsEnabled = false;

        try
        {
            var result = await _updateService.CheckForUpdateAsync();

            switch (result.Status)
            {
                case UpdateStatus.UpToDate:
                    UpdateNoticeText.Visibility = Visibility.Collapsed;
                    _pendingUpdateFileName = null;
                    _pendingUpdateDownloadUrl = null;
                    UiDialog.Show(this, result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;

                case UpdateStatus.UpdateAvailable:
                    _pendingUpdateFileName = result.FileName;
                    _pendingUpdateDownloadUrl = result.DownloadUrl;
                    UpdateNoticeText.Visibility = Visibility.Visible;
                    var sizeText = string.IsNullOrWhiteSpace(result.FileSize) ? ""
                        : $"\n文件大小：{result.FileSize}";
                    // 更新内容（大白话）从服务器 release-notes.json 取；没配就用兜底说明
                    var notes = result.Notes is { Count: > 0 } ? result.Notes : ["修复了一些问题，用起来更稳"];
                    var notesText = "\n\n本次更新内容：" + string.Join("", notes.Select(n => $"\n· {n}"));
                    var confirm = UiDialog.Show(this,
                        $"{result.Message}{sizeText}{notesText}\n\n是否立即下载更新？",
                        "发现新版本",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (confirm == MessageBoxResult.Yes)
                    {
                        await DownloadAndInstallAsync(result.FileName!, result.DownloadUrl);
                    }
                    break;

                default:
                    UiDialog.Show(this, result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private async Task DownloadAndInstallAsync(string fileName, string? downloadUrl)
    {
        var dlg = new UpdateDownloadWindow
        {
            Owner = this,
            FileName = fileName
        };
        dlg.Show();

        var progress = new Progress<int>(p => dlg.Progress = p);

        try
        {
            var filePath = await _updateService.DownloadUpdateAsync(fileName, downloadUrl, progress, dlg.CancellationToken);

            dlg.MarkCompleted();

            if (filePath is null)
            {
                dlg.Close();
                UiDialog.Show(this, "下载失败", "更新", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            await Task.Delay(500);
            dlg.Close();
            Close();
        }
        catch (OperationCanceledException)
        {
            dlg.Close();
        }
        catch (Exception ex)
        {
            dlg.Close();
            UiDialog.Show(this, $"下载失败：{ex.Message}", "更新", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void UpdateNotice_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingUpdateFileName))
        {
            return;
        }

        await DownloadAndInstallAsync(_pendingUpdateFileName, _pendingUpdateDownloadUrl);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsFromUpdateNotice(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        DragMove();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool IsFromUpdateNotice(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, UpdateNoticeText))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void AdjustNumber_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        static string Adjust(string? raw, int delta, int min = 0)
        {
            if (!int.TryParse(raw, out var value) || value < min)
            {
                value = min;
            }

            value += delta;
            if (value < min)
            {
                value = min;
            }

            return value.ToString();
        }

        if (tag == "Start:+1")
        {
            Vm.FileNameDeleteStartIndex = Adjust(Vm.FileNameDeleteStartIndex, +1);
            return;
        }

        if (tag == "Start:-1")
        {
            Vm.FileNameDeleteStartIndex = Adjust(Vm.FileNameDeleteStartIndex, -1);
            return;
        }

        if (tag == "Count:+1")
        {
            Vm.FileNameDeleteCount = Adjust(Vm.FileNameDeleteCount, +1);
            return;
        }

        if (tag == "Count:-1")
        {
            Vm.FileNameDeleteCount = Adjust(Vm.FileNameDeleteCount, -1);
            return;
        }

        if (tag == "SeqStart:+1")
        {
            Vm.SequenceStart = Adjust(Vm.SequenceStart, +1, 0);
            return;
        }

        if (tag == "SeqStart:-1")
        {
            Vm.SequenceStart = Adjust(Vm.SequenceStart, -1, 0);
            return;
        }

        if (tag == "SeqStep:+1")
        {
            Vm.SequenceStep = Adjust(Vm.SequenceStep, +1, 1);
            return;
        }

        if (tag == "SeqStep:-1")
        {
            Vm.SequenceStep = Adjust(Vm.SequenceStep, -1, 1);
            return;
        }

        if (tag == "SeqDigits:+1")
        {
            Vm.SequenceDigits = Adjust(Vm.SequenceDigits, +1, 1);
            return;
        }

        if (tag == "SeqDigits:-1")
        {
            Vm.SequenceDigits = Adjust(Vm.SequenceDigits, -1, 1);
        }
    }

    private bool _isDropHintVisible;

    private static bool HasFileDrop(System.Windows.DragEventArgs e)
        => e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);

    private void Window_PreviewDragEnter(object sender, System.Windows.DragEventArgs e)
        => UpdateDragFeedback(e);

    private void Window_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
        => UpdateDragFeedback(e);

    /// <summary>
    /// 拖拽反馈：必须显式设置 Effects 才会被系统接受为可放置目标。
    /// 注意不要先把 Effects 置为 None 再改（Win11 会按初始值提前判定为“禁止拖放”，
    /// 表现为鼠标一直显示红色禁止图标、Drop 不触发）。
    /// </summary>
    private void UpdateDragFeedback(System.Windows.DragEventArgs e)
    {
        if (Vm.N115.IsActive)
        {
            // 115 浏览模式下不接管拖入，避免文件被加进看不见的本地列表
            SetDropHintVisible(false);
            return;
        }

        if (!HasFileDrop(e))
        {
            // 非文件拖拽（例如表格列拖动、文本拖动）交还给控件自身处理
            SetDropHintVisible(false);
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Copy;
        e.Handled = true;
        SetDropHintVisible(true);
    }

    private void Window_PreviewDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        // 在窗口内部元素之间移动时也会触发 DragLeave，用坐标二次确认，避免提示闪烁
        var p = e.GetPosition(this);
        if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight)
        {
            SetDropHintVisible(false);
        }
    }

    private void Window_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        SetDropHintVisible(false);

        if (Vm.N115.IsActive)
        {
            e.Handled = true;
            return;
        }

        if (!HasFileDrop(e))
        {
            // 非文件拖拽不接管，保留控件原有行为
            return;
        }

        e.Handled = true;

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        AddPathsWithFeedback(paths);
    }

    private void SetDropHintVisible(bool visible)
    {
        if (_isDropHintVisible == visible)
        {
            return;
        }

        _isDropHintVisible = visible;
        DropHintBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
