using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
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
        TitleText.Text = $"NameTool v{_displayVersion} By Vic4728";

        Vm.N115.LoginPrompt = ShowN115LoginAsync;
        Vm.N115.FolderPicker = ShowN115FolderPickerAsync;
        Vm.N115.RenamePrompt = ShowN115RenameDialog;
        Vm.N115.SelectionCleared += ClearN115Selection;
        Vm.N115.SelectionRequested += SelectN115Items;
        Vm.N115.SortChanged += SyncN115SortArrows;
        Vm.FilesSortChanged += SyncLocalSortArrows;

        // 列宽记忆：拖完自动存，下次启动照旧。必须在 InitializeComponent 之后 —— 列是 XAML 声明的。
        ColumnWidthManager.Attach(LocalFileGrid, "local");
        ColumnWidthManager.Attach(N115Grid, "n115");

        Closing += (_, _) =>
        {
            // 关窗前把还在防抖窗口里的最后一次拖动落盘（不到 800ms 就关窗的情况）
            ColumnWidthManager.FlushAll();
            Vm.SaveSequenceTemplatePresets();
            Vm.N115.Exit();
        };
        Loaded += async (_, _) => await CheckUpdateOnStartupAsync();
    }

    // ---------------- 字幕内容编辑模式 ----------------

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
            MessageBox.Show(this, $"打开 115 网盘失败：{ex.Message}", "115 网盘", MessageBoxButton.OK, MessageBoxImage.Error);
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
    /// 选中一行会推送 SelectedFile，字幕文件会立刻切到编辑器，勾个选就跳走太难受。
    /// 这里把落在复选框上的点击拦下（隧道事件，先于 DataGrid 的行选择逻辑），并手动翻转勾选。
    /// </summary>
    private void LocalFileGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.CheckBox>(e.OriginalSource as DependencyObject) is not { } checkBox)
        {
            return;
        }

        checkBox.IsChecked = checkBox.IsChecked != true;
        e.Handled = true;
    }

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
                MessageBox.Show(this,
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
            MessageBox.Show(this, $"添加文件失败：{ex.Message}", "添加文件", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            Vm.OutputDirectory = dlg.FolderName;
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
                    MessageBox.Show(this, result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    var confirm = MessageBox.Show(this,
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
                    MessageBox.Show(this, result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show(this, "下载失败", "更新", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(this, $"下载失败：{ex.Message}", "更新", MessageBoxButton.OK, MessageBoxImage.Error);
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
