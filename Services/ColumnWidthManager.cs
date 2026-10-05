using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Diagnostics;

namespace NameTool.Services;

/// <summary>
/// 把「列宽可以手动拖」升级成「拖完自动记住，下次启动照旧」。
/// <para>
/// 用法：窗口构造完（<c>InitializeComponent</c> 之后、真正显示之前）对每张表调一次
/// <see cref="Attach"/>，关窗时调 <see cref="FlushAll"/> 落最后一把还没到点的改动。
/// </para>
/// <para>
/// 三个关键点：
/// </para>
/// <list type="number">
/// <item><b>恢复时机</b>：入参是 <see cref="DataGrid"/>，不能是 ViewModel —— 列是 XAML 里声明的，
/// 只有在 <c>InitializeComponent</c> 之后才存在；而宽度本身又属于「视图状态」，不该塞进 VM。</item>
/// <item><b>抓改动</b>：`DataGrid` 没有「列宽变了」事件。<c>DataGridColumn</c> 是 <see cref="DependencyObject"/>，
/// 所以用 <see cref="DependencyPropertyDescriptor"/> 监听 <c>WidthProperty</c> —— 拖动表头分隔线时
/// 它会连续触发（每像素一次），必须防抖，否则一次拖动就能写几百次盘。</item>
/// <item><b>别自我触发</b>：恢复和重置都会改 <c>Width</c>，会走同一条回调。用 <c>_suppress</c> 挡住，
/// 否则「重置为默认」会立刻把默认值当成用户调整再存一遍。</item>
/// </list>
/// <para>
/// 用表头当键（见 <see cref="ColumnWidthStore"/>），同一张表里表头必须唯一；这里按需做一次去重，
/// 重复表头的列退化成「按下标区分」，且会在重建时按 <c>Header</c> 命中第一个匹配项。
/// </para>
/// </summary>
public static class ColumnWidthManager
{
    /// <summary>
    /// 恢复时的宽度下限。拖到 0 宽会让整列「消失」且表头分隔线也点不到，
    /// 光靠鼠标很难救回来；小于这个值的记录一律当脏数据丢掉，回落默认宽度。
    /// </summary>
    public const double MinRestoreWidth = 32;

    /// <summary>改动后等这么久没再动才落盘（一次拖动结束基本就等于立即保存）。</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

    private static readonly Dictionary<DataGrid, string> Keys = new();
    private static readonly Dictionary<DataGrid, List<(DataGridColumn Column, DataGridLength Default)>> Defaults = new();
    private static readonly Dictionary<DataGrid, DispatcherTimer> Timers = new();

    /// <summary>
    /// 每张表「上一次已经落盘的样子」。
    /// <para>
    /// 必须有：星号列在布局时会被 DataGrid 重算一次并发出 <c>Width</c> 通知，**值其实没变**。
    /// 只靠事件触发就会在用户根本没碰过列宽的情况下，无声无息写出一份记录文件
    /// （校验里第一轮就抓到了）。比较签名 = 只有真的变了才写。
    /// </para>
    /// </summary>
    private static readonly Dictionary<DataGrid, string> LastSaved = new();

    private static bool _suppress;

    /// <summary>该表是否已接上「记住列宽」（校验用）。</summary>
    public static bool IsAttached(DataGrid grid) => grid is not null && Keys.ContainsKey(grid);

    /// <summary>该表在落盘文件里的键（未接线返回 null，校验用）。</summary>
    public static string? StoreKeyOf(DataGrid grid)
        => grid is not null && Keys.TryGetValue(grid, out var key) ? key : null;

    /// <summary>
    /// 接上「记住列宽」：先把 XAML 里的默认宽度记下来（重置要用），再按落盘记录恢复，
    /// 最后开始监听用户拖动。
    /// </summary>
    /// <param name="grid">要记住列宽的表。</param>
    /// <param name="storeKey">落盘键，如 <c>local</c> / <c>n115</c>。</param>
    /// <returns>实际恢复了几列（没有记录或记录全不合规时为 0）。</returns>
    public static int Attach(DataGrid grid, string storeKey)
    {
        if (grid is null || string.IsNullOrWhiteSpace(storeKey)) return 0;
        if (Keys.ContainsKey(grid)) return 0;   // 同一张表只接一次

        Keys[grid] = storeKey;
        Defaults[grid] = grid.Columns
            .Select(c => (Column: c, Default: c.Width))
            .ToList();

        var restored = 0;
        var saved = ColumnWidthStore.Load(storeKey);
        if (saved is { Count: > 0 })
        {
            restored = Apply(grid, saved);
            Debug.WriteLine($"[列宽] {storeKey}: 记录 {saved.Count} 列，实际恢复 {restored} 列");
        }

        foreach (var column in grid.Columns)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(
                DataGridColumn.WidthProperty, typeof(DataGridColumn));
            descriptor?.AddValueChanged(column, (_, _) => OnColumnWidthChanged(grid));
        }

        // 记下「刚接上时是什么样」：之后布局阶段那些没改变值的 Width 通知就不会写出文件
        LastSaved[grid] = Signature(Capture(grid));
        return restored;
    }

    /// <summary>把一张表的当前列宽读成可落盘的记录。</summary>
    public static List<ColumnWidthEntry> Capture(DataGrid grid)
    {
        var result = new List<ColumnWidthEntry>();
        if (grid is null) return result;

        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var column = grid.Columns[i];
            var unit = column.Width.UnitType;

            result.Add(new ColumnWidthEntry
            {
                Header = ColumnKey(column, i, grid),
                // Auto / SizeToCells / SizeToHeader 的 Value 无意义（构造时固定给 1），只有
                // Pixel / Star 才需要记住数字
                Value = unit is DataGridLengthUnitType.Pixel or DataGridLengthUnitType.Star
                    ? column.Width.Value
                    : 1,
                Unit = unit.ToString(),
            });
        }

        return result;
    }

    /// <summary>按记录恢复列宽（表头对不上的列原样不动）。返回实际改了几列。</summary>
    public static int Apply(DataGrid grid, IEnumerable<ColumnWidthEntry> entries)
    {
        if (grid is null || entries is null) return 0;

        var byHeader = new Dictionary<string, ColumnWidthEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Header)) continue;
            byHeader[entry.Header.Trim()] = entry;
        }

        var applied = 0;
        _suppress = true;
        try
        {
            for (var i = 0; i < grid.Columns.Count; i++)
            {
                var column = grid.Columns[i];
                if (!byHeader.TryGetValue(ColumnKey(column, i, grid), out var entry)) continue;

                var length = ToLength(entry);
                if (length is null) continue;   // 脏数据：保持 XAML 默认宽度

                column.Width = length.Value;
                applied++;
            }
        }
        finally
        {
            _suppress = false;
        }

        return applied;
    }

    /// <summary>
    /// 还原这张表在 XAML 里声明的默认列宽，并删掉落盘记录（下次启动也是默认布局）。
    /// 「列拖没了」的救命入口。
    /// </summary>
    public static bool ResetToDefault(DataGrid grid)
    {
        if (grid is null || !Defaults.TryGetValue(grid, out var defaults)) return false;

        // 先掐掉还在防抖窗口里的待写任务，否则它会在 800ms 后把文件又写回来，
        // 「重置」就等于白点了（值虽一样，但用户会看到记录文件阴魂不散）。
        if (Timers.TryGetValue(grid, out var timer)) timer.Stop();

        _suppress = true;
        try
        {
            foreach (var (column, width) in defaults)
            {
                if (!grid.Columns.Contains(column)) continue;
                column.Width = width;
            }
        }
        finally
        {
            _suppress = false;
        }

        if (Keys.TryGetValue(grid, out var key))
        {
            ColumnWidthStore.Remove(key);
        }

        LastSaved[grid] = Signature(Capture(grid));
        return true;
    }

    /// <summary>把这张表还没到点的改动立即落盘（关窗时用）。</summary>
    public static void Flush(DataGrid grid)
    {
        if (grid is null || !Keys.ContainsKey(grid)) return;

        if (Timers.TryGetValue(grid, out var timer)) timer.Stop();
        Persist(grid);
    }

    /// <summary>把所有已接线表的待写改动立即落盘。</summary>
    public static void FlushAll()
    {
        foreach (var grid in Keys.Keys.ToList()) Flush(grid);
    }

    private static void OnColumnWidthChanged(DataGrid grid)
    {
        if (_suppress) return;

        if (!Timers.TryGetValue(grid, out var timer))
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, grid.Dispatcher)
            {
                Interval = SaveDelay,
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Persist(grid);
            };
            Timers[grid] = timer;
        }

        // 拖动过程中每像素触发一次：重启计时器 = 松手后 800ms 才真正写盘
        timer.Stop();
        timer.Start();
    }

    private static void Persist(DataGrid grid)
    {
        if (!Keys.TryGetValue(grid, out var key)) return;

        var entries = Capture(grid);
        if (entries.Count == 0) return;   // 列都被清掉了（校验会这么干），没什么可记的

        var signature = Signature(entries);
        if (LastSaved.TryGetValue(grid, out var previous) && string.Equals(previous, signature, StringComparison.Ordinal))
        {
            return;   // 值没真变（布局阶段的星号列重算就是这种），不写盘
        }

        ColumnWidthStore.Save(key, entries);
        LastSaved[grid] = signature;
    }

    /// <summary>把一组列宽压成一个可比较的字符串，用来判断「是不是真的变了」。</summary>
    private static string Signature(IEnumerable<ColumnWidthEntry> entries)
        => string.Join('|', entries.Select(e => $"{e.Header}:{e.Unit}:{e.Value:0.####}"));

    /// <summary>把记录还原成 <c>DataGridLength</c>；明显不合法的返回 null（不覆盖默认值）。</summary>
    private static DataGridLength? ToLength(ColumnWidthEntry entry)
    {
        switch (entry.Unit)
        {
            case nameof(DataGridLengthUnitType.Auto):
                return DataGridLength.Auto;

            case nameof(DataGridLengthUnitType.SizeToCells):
                return new DataGridLength(1, DataGridLengthUnitType.SizeToCells);

            case nameof(DataGridLengthUnitType.SizeToHeader):
                return new DataGridLength(1, DataGridLengthUnitType.SizeToHeader);

            case nameof(DataGridLengthUnitType.Star):
                return IsUsable(entry.Value) ? new DataGridLength(entry.Value, DataGridLengthUnitType.Star) : null;

            default:
                // 未知单位按像素处理；比 MinRestoreWidth 还窄的一律当脏数据
                return IsUsable(entry.Value) && entry.Value >= MinRestoreWidth
                    ? new DataGridLength(entry.Value)
                    : null;
        }
    }

    private static bool IsUsable(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;

    /// <summary>列的表头文字（空表头退化成按下标，保证同一张表里键唯一）。</summary>
    private static string ColumnKey(DataGridColumn column, int index, DataGrid grid)
    {
        var text = column.Header as string ?? column.Header?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return $"#{index}";

        text = text.Trim();

        // 同一张表里表头重名时，后续的重名列加下标后缀，避免两列争一条记录
        var firstIndex = -1;
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var other = grid.Columns[i].Header as string ?? grid.Columns[i].Header?.ToString();
            if (string.Equals(other?.Trim(), text, StringComparison.Ordinal))
            {
                firstIndex = i;
                break;
            }
        }

        return firstIndex == index ? text : $"{text}#{index}";
    }
}
