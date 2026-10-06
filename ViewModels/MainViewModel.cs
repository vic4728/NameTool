using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using NameTool.Infrastructure;
using NameTool.Models;
using NameTool.Services;

namespace NameTool.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly TextBatchProcessor _processor = new();

    public ObservableCollection<FileItemViewModel> Files { get; } = [];
    public ObservableCollection<ReplaceRuleViewModel> Rules { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    /// <summary>
    /// 本地列表的排序状态。用户点过「原文件名」列头才为 true；
    /// 没点过时按完整路径排（同一文件夹的文件聚在一起），
    /// 点过之后一直保持用户选的方向，新增文件也插进当前顺序里。
    /// </summary>
    private bool _filesSortedByName;
    private bool _filesSortDescending;

    /// <summary>
    /// 文件名排序：自然排序（数字段按数值比，「副本 (2)」排在「副本 (10)」前面），
    /// 文字段按 zh-CN 拼音、忽略大小写。与 115 列表共用 <see cref="NaturalNameComparer"/>。
    /// </summary>
    private static readonly NaturalNameComparer FileNameComparer = NaturalNameComparer.Instance;

    /// <summary>本地排序状态变化（含添加文件时的「默认升序」重置），供窗口同步列头箭头。</summary>
    public event Action? FilesSortChanged;

    /// <summary>当前是否按名称排序（给窗口画箭头用）。</summary>
    public bool IsFilesSortedByName => _filesSortedByName;

    /// <summary>当前是否降序（给窗口画箭头用）。</summary>
    public bool IsFilesSortDescending => _filesSortDescending;

    private bool _recursiveAddFolder = true;

    /// <summary>正在批量改勾选状态（全选 / 全不选）——期间不逐行触发预览重算。</summary>
    private bool _bulkScopeUpdate;

    private bool _deleteLinesEnabled;
    private string _deleteStartLine = "20";
    private string _deleteEndLine = "37";
    private bool _removeEnglish;
    private bool _removeJapanese;
    private bool _removeKorean;
    private bool _makeBackup = true;   // 覆盖前默认留一份 .bak：误操作的唯一回滚手段（处理方式条已移除，保持默认常开）
    private string _outputDirectory = string.Empty;

    // ---- 编辑模式自动备份（用户要求：底部 [自动保存|"保存时间"秒]，默认开、默认 10 秒）----
    private bool _autoSaveEnabled = true;
    private bool _debugLogEnabled;
    private int _autoSaveSeconds = 10;

    /// <summary>自动备份上次写入的内容：没变化就不重复写盘。</summary>
    private string? _lastAutoBackupContent;

    /// <summary>
    /// 是否弹交互对话框（未保存提示 / 只读询问）。
    /// <para>默认 <c>false</c>：离屏校验 / 无界面环境必须静默走默认分支，否则 MessageBox 会卡死断言；
    /// <see cref="MainWindow"/> 构造时把它打开。</para>
    /// </summary>
    public bool UiPromptsEnabled { get; set; }

    /// <summary>编辑模式自动备份开关（默认开启）。</summary>
    public bool AutoSaveEnabled
    {
        get => _autoSaveEnabled;
        set => SetProperty(ref _autoSaveEnabled, value);
    }

    /// <summary>
    /// DEBUG 日志开关（用户要求，2026-10-06「日志调整到 DEBUG，记录所有操作返回信息」）：
    /// 开启后各操作的返回信息（接口返回码 / 写回结果 / 跳过原因等）以 [DEBUG] 级别进窗口与文件。
    /// 持久化在 ui-state.json（debug-log 键），重启记住。
    /// </summary>
    public bool DebugLogEnabled
    {
        get => _debugLogEnabled;
        set
        {
            if (SetProperty(ref _debugLogEnabled, value))
            {
                Services.FileLogSink.DebugEnabled = value;
                UiStateStore.SetBool("debug-log", value);
                Log(value ? "已开启 DEBUG 日志（记录所有操作返回信息）" : "已关闭 DEBUG 日志");
            }
        }
    }

    /// <summary>自动备份间隔（秒），默认 10，钳制到 1~86400。</summary>
    public int AutoSaveSeconds
    {
        get => _autoSaveSeconds;
        set => SetProperty(ref _autoSaveSeconds, Math.Clamp(value, 1, 86400));
    }

    private bool _fileNameAddEnabled;
    private string _fileNamePrefixAdd = string.Empty;
    private string _fileNameSuffixAdd = string.Empty;
    private string _fileNameAddAnchor = string.Empty;
    private string _fileNameAddContent = string.Empty;
    private bool _fileNameAddAfterAnchor = true;

    private bool _fileNameDeleteByIndexEnabled;
    private string _fileNameDeleteStartIndex = "1";
    private string _fileNameDeleteCount = "1";

    private bool _fileNameDeleteByAnchorEnabled;
    private string _fileNameDeleteAnchor = string.Empty;
    private bool _fileNameDeleteAfterAnchor = true;
    private string _fileNameDeleteAnchorCount = "1";

    private string _sequenceTemplate = string.Empty;
    private string _sequenceStart = "1";
    private string _sequenceStep = "1";
    private string _sequenceDigits = "2";
    private bool _sequencePadEnabled = true;
    private bool _sequenceAlphabetic;
    private bool _sequenceUppercase;

    private readonly Stack<SequenceUndoState> _sequenceUndoStack = new();
    private readonly Stack<AddUndoState> _addUndoStack = new();
    private readonly Stack<DeleteUndoState> _deleteUndoStack = new();
    private readonly List<RenameRollbackItem> _lastRenameRollbackItems = [];
    private bool _isApplyingUndo;

    private string? _selectedSequencePreset;
    private readonly string _sequencePresetFilePath;

    /// <summary>
    /// 数据目录统一由 <see cref="AppDataPaths"/> 解析（exe 同级可写就用它，否则落到
    /// <c>%LOCALAPPDATA%\NameTool</c>）。以前这里写死 <c>{exe}\data</c>，
    /// 装到 <c>Program Files</c> 时序号预设会静默存不下来。
    /// </summary>
    private static string DataDir => AppDataPaths.PrimaryDirectory;

    private FileItemViewModel? _selectedFile;
    private ReplaceRuleViewModel? _selectedRule;

    private DisplayMode _currentMode = DisplayMode.List;

    public MainViewModel()
    {
        _sequencePresetFilePath = Path.Combine(DataDir, "sequence-presets.json");

        // 落盘日志：数据目录下 logs\NameTool.log（上限 5MB，超限清空重记），与日志窗口同步记录
        FileLogSink.Configure(AppDataPaths.Combine(Path.Combine("logs", "NameTool.log")));
        FileLogSink.Write("—— 程序启动 ——");

        var initialRule = new ReplaceRuleViewModel();
        Rules.Add(initialRule);
        AttachRulePreviewRefresh(initialRule);
        SelectedRule = initialRule;

        AddRuleCommand = new RelayCommand(_ =>
        {
            var newRule = new ReplaceRuleViewModel();
            Rules.Add(newRule);
            AttachRulePreviewRefresh(newRule);
            SelectedRule = newRule;
            RaiseCommandsCanExecute();
            RefreshPreviewForAllFiles();
        });
        RemoveRuleCommand = new RelayCommand(_ => RemoveSelectedRule(), _ => Rules.Count > 1 && SelectedRule is not null);
        RemoveSelectedFileCommand = new RelayCommand(_ => RemoveSelectedFile(), _ => SelectedFile is not null);
        ClearFilesCommand = new RelayCommand(_ =>
        {
            Files.Clear();
            Log("已清空文件列表");
        }, _ => Files.Count > 0);
        RunCommand = new RelayCommand(_ => Run(fullProcess: true), _ => Files.Count > 0);
        RunReplaceOnlyCommand = new RelayCommand(_ => Run(fullProcess: false), _ => Files.Count > 0);

        // 「删除字幕外语」区里的开始处理：只按勾选的语言过滤正文并写回
        RunLanguageOnlyCommand = new RelayCommand(_ => RunLanguageOnly(), _ => Files.Count > 0);

        // 本地列表工具条上的「繁=>简」：文件名 + 字幕正文一起转简体，当场落地
        RunToSimplifiedCommand = new RelayCommand(_ => RunToSimplified(), _ => Files.Count > 0);

        // 字幕编辑工具条上的「保存」：把编辑器内容写回原文件（不套任何规则）；只读条目不可保存
        SaveSubtitleCommand = new RelayCommand(_ => SaveSelectedFile(), _ => SelectedFile is { IsTextFile: true, IsReadOnly: false });

        // 「批量替换」区的「应用到编辑器」：把替换规则用在当前编辑的文件内容上（编辑模式专属）
        ApplyRulesToEditorCommand = new RelayCommand(_ => ApplyRulesToEditor(), _ => SelectedFile is { IsTextFile: true, IsReadOnly: false });

        CheckAllFilesCommand = new RelayCommand(_ => SetAllChecked(true), _ => Files.Count > 0);
        UncheckAllFilesCommand = new RelayCommand(_ => SetAllChecked(false), _ => Files.Count > 0);

        // 勾选状态一变就要更新「已勾选 N/M」与预览里的序号；增删文件同理。
        // 挂在 CollectionChanged 上，添加/移除/清空/重排（Move）都会走到，不用每处单独记得调。
        Files.CollectionChanged += (_, _) =>
        {
            NotifyScopeChanged();
            RaiseCommandsCanExecute();
        };

        ReturnToListCommand = new RelayCommand(_ => ReturnToList(), _ => CurrentMode == DisplayMode.Editor);
        SaveSequenceTemplateCommand = new RelayCommand(_ => SaveSequenceTemplate(), _ => !string.IsNullOrWhiteSpace(SequenceTemplate));
        UndoSequenceCommand = new RelayCommand(_ => UndoSequence(), _ => _sequenceUndoStack.Count > 0 || _lastRenameRollbackItems.Count > 0);
        UndoAddCommand = new RelayCommand(_ => UndoAdd(), _ => _addUndoStack.Count > 0 || _lastRenameRollbackItems.Count > 0);
        UndoDeleteCommand = new RelayCommand(_ => UndoDelete(), _ => _deleteUndoStack.Count > 0 || _lastRenameRollbackItems.Count > 0);

        SequenceTemplatePresets.Add("自定义（在模板中使用 {n}）");
        foreach (var builtIn in BuiltInSequencePresetDisplayNames)
        {
            SequenceTemplatePresets.Add(builtIn);
        }
        N115 = new N115ViewModel(BuildRenamedName, Log);
        MigrateLegacyData();
        LoadSequenceTemplatePresets();
    }

    public RelayCommand AddRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }
    public RelayCommand RemoveSelectedFileCommand { get; }
    public RelayCommand ClearFilesCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand RunReplaceOnlyCommand { get; }

    /// <summary>「删除字幕外语」区里的「开始处理」。</summary>
    public RelayCommand RunLanguageOnlyCommand { get; }

    /// <summary>本地列表工具条上的「繁=&gt;简」：文件名与字幕正文一起繁体转简体。</summary>
    public RelayCommand RunToSimplifiedCommand { get; }

    /// <summary>字幕编辑工具条上的「保存」（另存为要弹对话框，走代码后置的 Click）。</summary>
    public RelayCommand SaveSubtitleCommand { get; }

    /// <summary>
    /// 「批量替换」区的「应用到编辑器」：把替换规则用在**当前编辑的文件内容**上并就地写回。
    /// 只在编辑模式有文本文件时可用 —— 编辑模式下用户关心的是手上这一个文件，不是整批。
    /// </summary>
    public RelayCommand ApplyRulesToEditorCommand { get; }

    /// <summary>本地列表：勾选全部文件 / 取消全部勾选。</summary>
    public RelayCommand CheckAllFilesCommand { get; }
    public RelayCommand UncheckAllFilesCommand { get; }

    public RelayCommand ReturnToListCommand { get; }
    public RelayCommand SaveSequenceTemplateCommand { get; }
    public RelayCommand UndoSequenceCommand { get; }
    public RelayCommand UndoAddCommand { get; }
    public RelayCommand UndoDeleteCommand { get; }

    /// <summary>115 网盘浏览 / 批量改名。首次使用时才弹登录窗口授权。</summary>
    public N115ViewModel N115 { get; }

    public ObservableCollection<string> SequenceTemplatePresets { get; } = [];

    /// <summary>
    /// 内置序号预设的**显示名**（下拉框里给用户看的）。落到 <see cref="SequenceTemplate"/> 的
    /// 永远是模板本身（经 <see cref="TryGetBuiltInSequenceTemplate"/> 翻译）；
    /// 保存 / 持久化时要把这些显示名排除 —— 它们不是合法模板，存下去下次就是一堆死条目。
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInSequencePresetDisplayNames =
    [
        "原名称-[数字序号]",
        "原名称-[英文小写序号]",
        "[文件名]-[修改日期]",
        "[文件名]-[文件类型]-[序号]",
    ];

    /// <summary>内置预设显示名 → 模板。纯函数，便于离线断言。</summary>
    public static bool TryGetBuiltInSequenceTemplate(string? displayName, out string template)
    {
        template = displayName switch
        {
            "原名称-[数字序号]" => "{name}-{n}",
            "原名称-[英文小写序号]" => "{name}-{a}",
            "[文件名]-[修改日期]" => "{name}-{date}",
            "[文件名]-[文件类型]-[序号]" => "{name}-{ext}-{n}",
            _ => string.Empty,
        };
        return template.Length > 0;
    }

    public string? SelectedSequencePreset
    {
        get => _selectedSequencePreset;
        set
        {
            if (SetProperty(ref _selectedSequencePreset, value) &&
                !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, "自定义（在模板中使用 {n}）", StringComparison.Ordinal))
            {
                // 内置预设显示名先翻译成模板；用户自己保存的预设本身就是模板字面
                SequenceTemplate = TryGetBuiltInSequenceTemplate(value, out var builtIn) ? builtIn : value;
            }
        }
    }

    public DisplayMode CurrentMode
    {
        get => _currentMode;
        set
        {
            if (SetProperty(ref _currentMode, value))
            {
                ReturnToListCommand?.RaiseCanExecuteChanged();
                // ⚠️ 进/出编辑器都要刷「替换（应用到编辑器）」——不刷的话按钮停在启动时的禁用态，
                // 表现就是「填了查找和替换内容也点不动替换按钮」（用户实测踩坑）
                ApplyRulesToEditorCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool RecursiveAddFolder
    {
        get => _recursiveAddFolder;
        set => SetProperty(ref _recursiveAddFolder, value);
    }

    public bool DeleteLinesEnabled
    {
        get => _deleteLinesEnabled;
        set => SetProperty(ref _deleteLinesEnabled, value);
    }

    public string DeleteStartLine
    {
        get => _deleteStartLine;
        set => SetProperty(ref _deleteStartLine, value);
    }

    public string DeleteEndLine
    {
        get => _deleteEndLine;
        set => SetProperty(ref _deleteEndLine, value);
    }

    public bool RemoveEnglish
    {
        get => _removeEnglish;
        set
        {
            if (SetProperty(ref _removeEnglish, value))
            {
                RefreshSelectedTextContentIfNeeded();
            }
        }
    }

    public bool RemoveJapanese
    {
        get => _removeJapanese;
        set
        {
            if (SetProperty(ref _removeJapanese, value))
            {
                RefreshSelectedTextContentIfNeeded();
            }
        }
    }

    public bool RemoveKorean
    {
        get => _removeKorean;
        set
        {
            if (SetProperty(ref _removeKorean, value))
            {
                RefreshSelectedTextContentIfNeeded();
            }
        }
    }

    /// <summary>
    /// 处理方式：**永远就地覆盖写回**。
    /// 「处理方式 / 选择目录」条已于 2026-10-05 按用户要求移除，输出到目录的模式随之下线；
    /// 保留只读属性是为了让 BuildOptions / 日志等处的判断不用跟着改。
    /// </summary>
    public bool InPlace => true;

    public bool MakeBackup
    {
        get => _makeBackup;
        set => SetProperty(ref _makeBackup, value);
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetProperty(ref _outputDirectory, value);
    }

    public bool FileNameAddEnabled
    {
        get => _fileNameAddEnabled;
        set
        {
            if (SetProperty(ref _fileNameAddEnabled, value))
            {
                RefreshPreviewForAllFiles();
            }
        }
    }

    public string FileNamePrefixAdd
    {
        get => _fileNamePrefixAdd;
        set
        {
            PushAddUndoIfNeeded(_fileNamePrefixAdd, value);
            if (SetProperty(ref _fileNamePrefixAdd, value))
            {
                RefreshPreviewForAllFiles();
                UndoAddCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FileNameSuffixAdd
    {
        get => _fileNameSuffixAdd;
        set
        {
            PushAddUndoIfNeeded(_fileNameSuffixAdd, value);
            if (SetProperty(ref _fileNameSuffixAdd, value))
            {
                RefreshPreviewForAllFiles();
                UndoAddCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FileNameAddAnchor
    {
        get => _fileNameAddAnchor;
        set
        {
            PushAddUndoIfNeeded(_fileNameAddAnchor, value);
            if (SetProperty(ref _fileNameAddAnchor, value))
            {
                RefreshPreviewForAllFiles();
                UndoAddCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FileNameAddContent
    {
        get => _fileNameAddContent;
        set
        {
            PushAddUndoIfNeeded(_fileNameAddContent, value);
            if (SetProperty(ref _fileNameAddContent, value))
            {
                RefreshPreviewForAllFiles();
                UndoAddCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool FileNameAddAfterAnchor
    {
        get => _fileNameAddAfterAnchor;
        set
        {
            PushAddUndoIfNeeded(_fileNameAddAfterAnchor, value);
            if (SetProperty(ref _fileNameAddAfterAnchor, value))
            {
                RefreshPreviewForAllFiles();
                RaisePropertyChanged(nameof(FileNameAddBeforeAnchor));
                UndoAddCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool FileNameAddBeforeAnchor
    {
        get => !FileNameAddAfterAnchor;
        set
        {
            if (value)
            {
                FileNameAddAfterAnchor = false;
            }
        }
    }

    public bool FileNameDeleteByIndexEnabled
    {
        get => _fileNameDeleteByIndexEnabled;
        set
        {
            PushDeleteUndoIfNeeded(_fileNameDeleteByIndexEnabled, value);
            if (SetProperty(ref _fileNameDeleteByIndexEnabled, value))
            {
                RefreshPreviewForAllFiles();
                UndoDeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FileNameDeleteStartIndex
    {
        get => _fileNameDeleteStartIndex;
        set
        {
            PushDeleteUndoIfNeeded(_fileNameDeleteStartIndex, value);
            if (SetProperty(ref _fileNameDeleteStartIndex, value))
            {
                RefreshPreviewForAllFiles();
                UndoDeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FileNameDeleteCount
    {
        get => _fileNameDeleteCount;
        set
        {
            PushDeleteUndoIfNeeded(_fileNameDeleteCount, value);
            if (SetProperty(ref _fileNameDeleteCount, value))
            {
                RefreshPreviewForAllFiles();
                UndoDeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool FileNameDeleteByAnchorEnabled
    {
        get => _fileNameDeleteByAnchorEnabled;
        set
        {
            PushDeleteUndoIfNeeded(_fileNameDeleteByAnchorEnabled, value);
            if (SetProperty(ref _fileNameDeleteByAnchorEnabled, value))
            {
                RefreshPreviewForAllFiles();
                UndoDeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FileNameDeleteAnchor
    {
        get => _fileNameDeleteAnchor;
        set
        {
            PushDeleteUndoIfNeeded(_fileNameDeleteAnchor, value);
            if (SetProperty(ref _fileNameDeleteAnchor, value))
            {
                RefreshPreviewForAllFiles();
                UndoDeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool FileNameDeleteAfterAnchor
    {
        get => _fileNameDeleteAfterAnchor;
        set
        {
            PushDeleteUndoIfNeeded(_fileNameDeleteAfterAnchor, value);
            if (SetProperty(ref _fileNameDeleteAfterAnchor, value))
            {
                RefreshPreviewForAllFiles();
                UndoDeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FileNameDeleteAnchorCount
    {
        get => _fileNameDeleteAnchorCount;
        set
        {
            PushDeleteUndoIfNeeded(_fileNameDeleteAnchorCount, value);
            if (SetProperty(ref _fileNameDeleteAnchorCount, value))
            {
                RefreshPreviewForAllFiles();
                UndoDeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SequenceTemplate
    {
        get => _sequenceTemplate;
        set
        {
            PushSequenceUndoIfNeeded(_sequenceTemplate, value);
            if (SetProperty(ref _sequenceTemplate, value))
            {
                RefreshPreviewForAllFiles();
                SaveSequenceTemplateCommand.RaiseCanExecuteChanged();
                UndoSequenceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SequenceStart
    {
        get => _sequenceStart;
        set
        {
            PushSequenceUndoIfNeeded(_sequenceStart, value);
            if (SetProperty(ref _sequenceStart, value))
            {
                RefreshPreviewForAllFiles();
                UndoSequenceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SequenceStep
    {
        get => _sequenceStep;
        set
        {
            PushSequenceUndoIfNeeded(_sequenceStep, value);
            if (SetProperty(ref _sequenceStep, value))
            {
                RefreshPreviewForAllFiles();
                UndoSequenceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SequenceDigits
    {
        get => _sequenceDigits;
        set
        {
            PushSequenceUndoIfNeeded(_sequenceDigits, value);
            if (SetProperty(ref _sequenceDigits, value))
            {
                RefreshPreviewForAllFiles();
                UndoSequenceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool SequencePadEnabled
    {
        get => _sequencePadEnabled;
        set
        {
            PushSequenceUndoIfNeeded(_sequencePadEnabled, value);
            if (SetProperty(ref _sequencePadEnabled, value))
            {
                RefreshPreviewForAllFiles();
                UndoSequenceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool SequenceAlphabetic
    {
        get => _sequenceAlphabetic;
        set
        {
            PushSequenceUndoIfNeeded(_sequenceAlphabetic, value);
            if (SetProperty(ref _sequenceAlphabetic, value))
            {
                RefreshPreviewForAllFiles();
                UndoSequenceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool SequenceUppercase
    {
        get => _sequenceUppercase;
        set
        {
            PushSequenceUndoIfNeeded(_sequenceUppercase, value);
            if (SetProperty(ref _sequenceUppercase, value))
            {
                RefreshPreviewForAllFiles();
                UndoSequenceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public FileItemViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                RemoveSelectedFileCommand.RaiseCanExecuteChanged();
                SaveSubtitleCommand.RaiseCanExecuteChanged();
                // ⚠️ 这条不刷的话，「替换（应用到编辑器）」按钮在进编辑器后仍是启动时的禁用态 —— 用户点不动，即「查找替换无效」
                ApplyRulesToEditorCommand.RaiseCanExecuteChanged();
                EditorStatusText = string.Empty;   // 换文件了，上一条「已保存」提示不该留着

                // 2026-10-05 起：**选中不再自动进编辑器**（用户要求改为双击 / 点状态列编辑图标）。
                // 显式进入编辑器走 OpenInEditor(...)。
            }
        }
    }

    public ReplaceRuleViewModel? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (SetProperty(ref _selectedRule, value))
            {
                RemoveRuleCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    private string _editorStatusText = string.Empty;

    /// <summary>字幕编辑工具条上的即时反馈（保存 / 另存为的结果）。切换文件或返回列表时清空。</summary>
    public string EditorStatusText
    {
        get => _editorStatusText;
        private set => SetProperty(ref _editorStatusText, value);
    }

    /// <summary>给窗口代码后置用的状态栏写入口（查找按钮等直接操作编辑器的功能在此报结果）。</summary>
    public void SetEditorStatus(string text) => EditorStatusText = text;

    // ---------------- 编码转换（字幕编辑模式工具条下拉） ----------------

    /// <summary>占位项：下拉框默认停在这一项，选到具体编码才触发转换。</summary>
    public const string EncodingPlaceholder = "转换为编码…";

    /// <summary>编码下拉的选项（占位项在最前，转换完成后弹回它，防误触发）。</summary>
    public static string[] EncodingChoices { get; } =
    [
        EncodingPlaceholder,
        "UTF-8",
        "UTF-8（带 BOM）",
        "GB2312（简体中文）",
        "GBK（简体中文扩展）",
        "GB18030（简体中文全集）",
        "Big5（繁体中文）",
        "UTF-16 LE（Windows）",
        "UTF-16 BE",
    ];

    private string _selectedEncodingChoice = EncodingPlaceholder;

    /// <summary>下拉选中项；选中具体编码即执行转换，完成后弹回占位项。</summary>
    public string SelectedEncodingChoice
    {
        get => _selectedEncodingChoice;
        set
        {
            if (!SetProperty(ref _selectedEncodingChoice, value)) return;
            if (value is null || value == EncodingPlaceholder) return;

            var ok = ConvertSelectedFileEncoding(value);
            // 无论成败都弹回占位：同一项再选能再次触发，也避免「显示的编码」被误解成当前文件编码
            SetProperty(ref _selectedEncodingChoice, EncodingPlaceholder, nameof(SelectedEncodingChoice));
            _ = ok;
        }
    }

    /// <summary>显示名 → 编码实例（找不到的名称返回 null，理论上不会发生）。</summary>
    private static Encoding? ResolveEncodingByName(string displayName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return displayName switch
        {
            "UTF-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "UTF-8（带 BOM）" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "GB2312（简体中文）" => Encoding.GetEncoding("gb2312"),
            "GBK（简体中文扩展）" => Encoding.GetEncoding("gbk"),
            "GB18030（简体中文全集）" => Encoding.GetEncoding("gb18030"),
            "Big5（繁体中文）" => Encoding.GetEncoding("big5"),
            "UTF-16 LE（Windows）" => Encoding.Unicode,
            "UTF-16 BE" => Encoding.BigEndianUnicode,
            _ => null,
        };
    }

    /// <summary>
    /// 把**编辑器当前内容**用目标编码写回原文件（内容字符串不变，只改盘上的字节编码）。
    /// 有损拦截：内容里有目标编码表示不了的字符（如 GB2312 装不下生僻字）时取消，
    /// 提示换更大的编码（GBK / GB18030），绝不静默写成 ? 号。
    /// </summary>
    public bool ConvertSelectedFileEncoding(string displayName)
    {
        var item = SelectedFile;
        if (item is null || !item.IsTextFile)
        {
            EditorStatusText = "二进制文件不能转换编码（只支持文本文件）";
            return false;
        }

        if (item.IsReadOnly)
        {
            EditorStatusText = "只读文件不能转换编码（没有写入权限）";
            return false;
        }

        var target = ResolveEncodingByName(displayName);
        if (target is null)
        {
            EditorStatusText = "未知的编码：" + displayName;
            return false;
        }

        var content = item.Content ?? string.Empty;

        if (!TextBatchProcessor.CanEncodeAll(target, content))
        {
            EditorStatusText = $"「{displayName}」装不下文件里的部分字符，转换已取消（可改用 GBK / GB18030）";
            Log($"编码转换取消：{item.OriginalFileName} → {displayName}（存在无法表示的字符）");
            return false;
        }

        try
        {
            var dir = Path.GetDirectoryName(item.FilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var newline = content.Contains("\r\n") ? "\r\n" : "\n";
            WriteAtomic(item.FilePath, content, target, newline);

            item.CurrentEncoding = target;
            item.MarkSaved();  // 盘上内容已与编辑器一致

            EditorStatusText = $"已转换为 {displayName}";
            Log($"编码转换：{item.FilePath} → {displayName}");
            return true;
        }
        catch (Exception ex)
        {
            EditorStatusText = "编码转换失败：" + ex.Message;
            Log($"编码转换失败：{item.FilePath} → {displayName} - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 本地列表的处理范围提示（显示在列表下方的范围条里）。
    /// 一个都没勾选时明确写出「将处理全部」，免得「取消勾选」被误解成「不处理」。
    /// </summary>
    public string ScopeSummary
    {
        get
        {
            if (Files.Count == 0) return "尚未添加文件";

            var checkedCount = Files.Count(x => x.IsChecked);
            return checkedCount == 0
                ? $"未勾选任何文件 → 将处理全部 {Files.Count} 个文件"
                : $"已勾选 {checkedCount} / 共 {Files.Count} 个文件（只有勾选的会被处理）";
        }
    }

    /// <summary>一个都没勾选时，范围条用警示色显示。</summary>
    public bool ScopeWarning => Files.Count > 0 && Files.All(x => !x.IsChecked);

    /// <summary>「全选 / 全不选」。批量改时屏蔽逐行的预览重算，最后统一刷一次。</summary>
    private void SetAllChecked(bool value)
    {
        _bulkScopeUpdate = true;
        try
        {
            foreach (var file in Files) file.IsChecked = value;
        }
        finally
        {
            _bulkScopeUpdate = false;
        }

        NotifyScopeChanged();
        RefreshPreviewForAllFiles();
    }

    private void NotifyScopeChanged()
    {
        RaisePropertyChanged(nameof(ScopeSummary));
        RaisePropertyChanged(nameof(ScopeWarning));
    }

    /// <summary>勾选状态变化 ⇒ 范围提示与预览序号都要跟着变。</summary>
    private void FileOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileItemViewModel.IsChecked)) return;
        if (_bulkScopeUpdate) return;      // 「全选 / 全不选」逐行改，别每行都重算一遍

        NotifyScopeChanged();
        RefreshPreviewForAllFiles();
    }

    /// <summary>
    /// 添加文件/文件夹（文件对话框、拖拽共用）。返回实际新增的文件数。
    /// 单个路径出错不会中断整体添加，只记录到日志。
    /// </summary>
    public int AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var path = raw.Trim();
            if (!visited.Add(path)) continue;

            try
            {
                if (Directory.Exists(path))
                {
                    var search = RecursiveAddFolder ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    foreach (var file in EnumerateFilesSafe(path, search))
                    {
                        if (TryAddFile(file)) added++;
                        else skipped++;
                    }

                    continue;
                }

                if (TryAddFile(path)) added++;
                else skipped++;
            }
            catch (Exception ex)
            {
                problems.Add($"{path}（{ex.Message}）");
            }
        }

        // 拖入 / 添加文件后回到「按原文件名升序」（用户要求：拖入默认升序）。
        // 用户之后点列头仍可切降序，但下一次拖入又回到升序。
        _filesSortedByName = true;
        _filesSortDescending = false;
        FilesSortChanged?.Invoke();

        SortFiles();

        if (added > 0 || skipped > 0 || problems.Count > 0 || Files.Count == 0)
        {
            var message = $"已添加 {added} 个文件（总计 {Files.Count}）";
            if (skipped > 0)
            {
                message += $"，跳过 {skipped} 个（重复或无法读取）";
            }

            Log(message);
        }

        foreach (var problem in problems)
        {
            Log($"[跳过] 无法读取：{problem}");
        }

        RaiseCommandsCanExecute();
        return added;
    }

    /// <summary>
    /// 安全遍历目录：遇到无权限/不可访问的子目录或符号链接时跳过，
    /// 避免拖动文件夹时因单个子目录异常导致整个拖入操作失败。
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafe(string root, SearchOption option)
    {
        var pending = new Queue<string>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var dir = pending.Dequeue();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (option != SearchOption.AllDirectories)
            {
                continue;
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var subDir in subDirs)
            {
                try
                {
                    // 跳过符号链接/联接点，避免循环递归
                    if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }

                pending.Enqueue(subDir);
            }
        }
    }

    private bool TryAddFile(string path)
    {
        if (!File.Exists(path)) return false;
        // 任意扩展名都收（含无扩展名的文件）：字幕 / 文本类可编辑内容，其余仅改名 / 复制
        if (Files.Any(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase))) return false;
        var item = new FileItemViewModel { FilePath = path };

        // 写入权限检测：文件 / 所在目录写不了 ⇒ 弹窗问用户「只读模式加入还是不加」。
        // 无界面环境（离屏校验）不弹窗，静默按只读加入，保证行为可断言。
        if (!TextBatchProcessor.CanWriteFile(path))
        {
            var fileName = Path.GetFileName(path);
            if (UiPromptsEnabled)
            {
                var answer = UiDialog.Show(
                    $"文件「{fileName}」或其所在目录没有写入权限，无法保存修改。\n\n" +
                    "是 = 以只读模式加入列表（可查看内容，不能保存）\n" +
                    "否 = 不添加该文件",
                    "只读模式", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return false;
            }

            item.IsReadOnly = true;
            Log($"[只读] {fileName} 没有写入权限，以只读模式加入");
        }

        // 勾选状态变化要驱动「处理范围」提示与预览序号重算
        item.PropertyChanged += FileOnPropertyChanged;

        if (item.IsTextFile)
        {
            var (content, encoding, newline) = TextBatchProcessor.ReadTextFileContent(item.FilePath);
            if (content is not null)
            {
                var nl = newline ?? "\n";
                item.SetOriginalContent(content);   // 语言过滤的基准：磁盘上的原文
                item.CurrentEncoding = encoding;    // 保存 / 编码转换沿用本次判定的编码
                var filtered = TextBatchProcessor.ProcessTextContent(content, RemoveEnglish, RemoveJapanese, RemoveKorean, nl);
                item.SetProgrammaticContent(filtered);
                item.Mode = DisplayMode.Editor;

                var lineCount = CountLines(filtered, nl);
                item.Preview = $"已加载内容（{lineCount}行）";
            }
            else
            {
                item.Preview = "读取失败";
            }
        }
        else
        {
            // 新文件默认勾选、排在末尾，所以它的处理序号就是「当前勾选数」（没有勾选时按全部算）
            var checkedCount = Files.Count(x => x.IsChecked);
            item.Preview = BuildPreviewText(item, checkedCount == 0 ? Files.Count : checkedCount);
        }

        item.IsProcessing = false;
        item.IsSuccess = null;
        Files.Add(item);
        RaiseCommandsCanExecute();
        return true;
    }

    /// <summary>
    /// 从字幕内容编辑模式回到文件列表（编辑工具条上的「返回列表」按钮、以及 Esc 都走这里）。
    /// <para>
    /// 必须把 <see cref="SelectedFile"/> 清空：列表的 SelectedItem 与它是双向绑定，
    /// 不清空的话那一行仍处于选中状态，用户再点同一行时 SelectedItem 没有变化、setter 不触发，
    /// 编辑器就再也打不开了（只能先点别的文件再点回来）。内容本身仍留在那一行的
    /// <c>Content</c> 里，重新打开不会丢。
    /// </para>
    /// </summary>
    private void ReturnToList()
    {
        // 改动没保存就退回列表：先问一声（保存 / 不保存 / 取消），别让用户手滑丢内容
        if (!ConfirmDiscardOrSaveOnExit()) return;

        EditorStatusText = string.Empty;

        if (SelectedFile is not null)
        {
            SelectedFile.Mode = DisplayMode.List;
            SelectedFile = null;
        }

        CurrentMode = DisplayMode.List;
    }

    /// <summary>
    /// 自动备份文件路径：与原文件同目录的「原文件名_bak.原扩展名」（如 subtitle.srt → subtitle_bak.srt）。
    /// </summary>
    public static string AutoBackupPath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(filePath) + "_bak" + Path.GetExtension(filePath));
    }

    /// <summary>
    /// 自动备份：把编辑器里**未保存**的内容写到 <see cref="AutoBackupPath"/>（沿用当前编码）。
    /// 内容与上次备份一致时不重复写盘。只读条目 / 非文本 / 没改过一律不动。
    /// </summary>
    /// <returns>true = 磁盘上现在有一份与编辑器一致的备份（或本就有且没变）。</returns>
    public bool WriteAutoBackupNow(FileItemViewModel item)
    {
        if (item is not { IsTextFile: true, IsReadOnly: false }) return false;
        if (!item.IsContentEdited) return false;

        var content = item.Content ?? string.Empty;
        if (string.Equals(content, _lastAutoBackupContent, StringComparison.Ordinal)) return true;

        try
        {
            var encoding = item.CurrentEncoding
                ?? TextBatchProcessor.ReadTextWithEncodingFallback(item.FilePath).Encoding
                ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var bakPath = AutoBackupPath(item.FilePath);
            File.WriteAllText(bakPath, content, encoding);
            _lastAutoBackupContent = content;
            Log($"[自动保存] 已备份「{item.OriginalFileName}」→ {Path.GetFileName(bakPath)}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[自动保存] 备份失败：{item.OriginalFileName} - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 删除自动备份文件（手动保存 / 不保存退出时调用）。
    /// 程序崩溃、被强杀时走不到这里 —— 备份会留在磁盘上，这正是设计意图。
    /// </summary>
    public void DeleteAutoBackup(FileItemViewModel item)
    {
        _lastAutoBackupContent = null;
        try
        {
            var bakPath = AutoBackupPath(item.FilePath);
            if (File.Exists(bakPath))
            {
                File.Delete(bakPath);
                Log($"[自动保存] 已删除备份 {Path.GetFileName(bakPath)}");
            }
        }
        catch (Exception ex)
        {
            Log($"[自动保存] 删除备份失败：{ex.Message}");
        }
    }

    /// <summary>「未保存的修改」三选一弹窗（是=保存 / 否=不保存 / 取消=留下）。</summary>
    private MessageBoxResult AskUnsavedChanges()
    {
        return UiDialog.Show(
            $"文件「{SelectedFile!.OriginalFileName}」已修改但尚未保存，是否保存？\n\n" +
            "是 = 保存并继续\n" +
            "否 = 不保存（删除自动备份文件）\n" +
            "取消 = 留在编辑器",
            "未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
    }

    /// <summary>
    /// 退出编辑模式 / 关闭程序前的未保存确认。
    /// 返回 true = 可以继续（已保存 / 放弃保存 / 本来就没改）；false = 用户取消，留在编辑器。
    /// </summary>
    public bool ConfirmDiscardOrSaveOnExit()
    {
        if (!UiPromptsEnabled || SelectedFile is not { IsContentEdited: true, IsReadOnly: false }) return true;

        var choice = AskUnsavedChanges();
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) return SaveSelectedFile();

        DeleteAutoBackup(SelectedFile);   // 「取消保存」：备份也一并清掉
        return true;
    }

    /// <summary>
    /// 字幕编辑模式下的「保存」：把编辑器里的内容**原样**写回当前文件（保持原编码）。
    /// 与「开始处理」的区别：这里不套任何规则，你看到什么就存什么。
    /// </summary>
    /// <summary>
    /// 「批量替换」区的「应用到编辑器」：把左侧替换规则应用到**正在编辑的这个文件**的当前内容上，
    /// 就地写回（沿用当前编码）并刷新编辑器快照。不涉及改名 / 删行 / 删语言 ——
    /// 那些回列表模式用各组的「开始」。写回前 .bak 备份照常生效。
    /// </summary>
    public bool ApplyRulesToEditor()
    {
        var item = SelectedFile;
        if (item is null || !item.IsTextFile)
        {
            EditorStatusText = "先在编辑模式里打开一个文本文件";
            return false;
        }

        if (item.IsReadOnly)
        {
            EditorStatusText = "只读文件不能应用规则（没有写入权限）";
            return false;
        }

        var rules = BuildRules(out var ruleError);
        if (ruleError is not null)
        {
            EditorStatusText = ruleError;
            Log($"编辑器替换未执行：{ruleError}");
            return false;
        }

        if (rules.Count == 0)
        {
            EditorStatusText = "替换规则为空：先在「批量替换」里填写查找内容";
            return false;
        }

        var content = item.Content ?? string.Empty;
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var replaced = TextBatchProcessor.ApplyReplaceRules(content, rules);
        if (string.Equals(replaced, content, StringComparison.Ordinal))
        {
            EditorStatusText = "替换规则没有匹配到内容（未写回）";
            Log($"编辑器替换：{item.OriginalFileName} 规则无匹配，内容未变");
            return true;
        }

        try
        {
            if (MakeBackup)
            {
                File.Copy(item.FilePath, NextAvailablePath(item.FilePath + ".bak"), overwrite: true);
            }

            var encoding = item.CurrentEncoding
                ?? TextBatchProcessor.ReadTextWithEncodingFallback(item.FilePath).Encoding
                ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            WriteAtomic(item.FilePath, replaced, encoding, newline);

            // 磁盘已与编辑器一致：基准快照对齐（等同 MarkSaved），否则再点「保存」会把替换结果写回去之前的状态
            item.SetProgrammaticContent(replaced);
            item.SetOriginalContent(replaced);
            item.IsSuccess = true;
            EditorStatusText = $"已按 {rules.Count} 条替换规则改写并保存";
            Log($"编辑器替换：{item.OriginalFileName}（{rules.Count} 条规则）已写回原文件");
            return true;
        }
        catch (Exception ex)
        {
            EditorStatusText = "替换写回失败：" + ex.Message;
            Log($"编辑器替换失败：{item.FilePath} - {ex.Message}");
            return false;
        }
    }

    public bool SaveSelectedFile()
    {
        var item = SelectedFile;
        if (item is null)
        {
            EditorStatusText = "没有可保存的文件";
            return false;
        }

        if (!item.IsTextFile)
        {
            EditorStatusText = "二进制文件没有可编辑的文本内容";
            return false;
        }

        if (item.IsReadOnly)
        {
            EditorStatusText = "只读文件不能保存（没有写入权限）";
            return false;
        }

        return SaveTextTo(item, item.FilePath, asNewFile: false);
    }

    /// <summary>
    /// 给状态栏 / 日志用的编码短标签（如「UTF-8」「GB2312/GBK」「UTF-8（BOM）」）。
    /// 落盘用哪个编码一目了然，编码转换后才好当场确认「保存按转换后的编码写了」。
    /// public static：FileItemViewModel.EncodingLabel 也要用它（编辑器底部状态栏绑定）。
    /// </summary>
    public static string DescribeEncoding(Encoding encoding)
    {
        var name = encoding.CodePage switch
        {
            65001 => "UTF-8",
            936 => "GB2312/GBK",
            54936 => "GB18030",
            950 => "Big5",
            1200 => "UTF-16 LE",
            1201 => "UTF-16 BE",
            _ => encoding.WebName
        };

        return encoding.GetPreamble().Length > 0 ? $"{name}（BOM）" : name;
    }

    /// <summary>
    /// 「另存为」：把编辑器里的内容写到指定文件，**原文件不动**；
    /// 列表里的条目也不改路径（它仍然指向上面的原文件）。
    /// </summary>
    public bool SaveSelectedFileAs(string targetPath)
    {
        var item = SelectedFile;
        if (item is null || string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        return SaveTextTo(item, targetPath, asNewFile: true);
    }

    private bool SaveTextTo(FileItemViewModel item, string targetPath, bool asNewFile)
    {
        try
        {
            // 沿用拖入时判定的编码；没有（老列表条目 / 异常路径）才重新检测，最后兜底 UTF-8
            var encoding = item.CurrentEncoding;
            encoding ??= TextBatchProcessor.ReadTextWithEncodingFallback(item.FilePath).Encoding;
            encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            WriteAtomic(targetPath, item.Content ?? string.Empty, encoding, "\n");

            if (!asNewFile)
            {
                // 磁盘内容已与编辑器一致：基准快照对齐，编辑标记清零
                item.MarkSaved();
                item.IsSuccess = true;
            }

            // 手动保存成功 ⇒ 自动备份的使命完成，删掉它（另存为同样算手动保存）
            DeleteAutoBackup(item);

            var name = Path.GetFileName(targetPath);
            // 把实际落盘的编码亮出来：编码转换后保存，用户能当场确认写的是转换后的编码
            var encLabel = DescribeEncoding(encoding);
            EditorStatusText = asNewFile ? $"已另存为 {name}（编码：{encLabel}）" : $"已保存 {name}（编码：{encLabel}）";
            Log(asNewFile ? $"另存为：{targetPath}（编码：{encLabel}）" : $"保存：{targetPath}（编码：{encLabel}）");
            return true;
        }
        catch (Exception ex)
        {
            EditorStatusText = "保存失败：" + ex.Message;
            Log($"保存失败：{targetPath} - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 勾选 / 取消「删除字幕外语」里的语言后，立即把**正在编辑的这个文件**的内容重算一遍。
    /// 从磁盘重新读，所以这里也会刷新「原始正文」快照。
    /// </summary>
    private void RefreshSelectedTextContentIfNeeded()
    {
        var item = SelectedFile;
        if (item is null) return;
        if (!item.IsTextFile) return;
        if (CurrentMode != DisplayMode.Editor) return;
        if (item.Mode != DisplayMode.Editor) return;

        var (content, _, newline) = TextBatchProcessor.ReadTextFileContent(item.FilePath);
        if (content is null) return;

        item.SetOriginalContent(content);
        item.SetProgrammaticContent(
            TextBatchProcessor.ProcessTextContent(content, RemoveEnglish, RemoveJapanese, RemoveKorean, newline ?? "\n"));
    }

    /// <summary>
    /// 处理时该用哪一份正文。
    /// <list type="bullet">
    /// <item>用户**没手工改过** ⇒ 回到拖入时的原始正文，按当前语言勾选重新推导 ——
    /// 这样「先勾选英文再拖入、之后又取消勾选」也不会留下被误删的行。</item>
    /// <item>用户**手工改过** ⇒ 以编辑器里的内容为准，绝不覆盖他的编辑。</item>
    /// </list>
    /// </summary>
    private static string ResolveBaseContent(FileItemViewModel file)
        => file.IsContentEdited ? file.Content : file.OriginalContent ?? file.Content;

    private static int CountLines(string? text, string? newline)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var nl = string.IsNullOrEmpty(newline) ? "\n" : newline;
        var count = 1;
        var idx = 0;
        while (true)
        {
            idx = text.IndexOf(nl, idx, StringComparison.Ordinal);
            if (idx < 0) break;
            count++;
            idx += nl.Length;
        }

        return count;
    }

    private static ProcessOptions BuildOptionsWithSequence(ProcessOptions source, string sequenceValue)
    {
        return new ProcessOptions
        {
            Rules = source.Rules,
            DeleteLinesEnabled = source.DeleteLinesEnabled,
            DeleteStartLine = source.DeleteStartLine,
            DeleteEndLine = source.DeleteEndLine,
            RemoveEnglish = source.RemoveEnglish,
            RemoveJapanese = source.RemoveJapanese,
            RemoveKorean = source.RemoveKorean,
            // 注意：新增 ProcessOptions 字段时必须在这里补一行 ——
            // 少拷一个开关，对应的处理会「静默不生效」（繁=>简 就踩过这个坑）
            ToSimplified = source.ToSimplified,
            InPlace = source.InPlace,
            MakeBackup = source.MakeBackup,
            OutputDirectory = source.OutputDirectory,

            FileNameAddEnabled = source.FileNameAddEnabled,
            FileNamePrefixAdd = source.FileNamePrefixAdd,
            FileNameSuffixAdd = source.FileNameSuffixAdd,
            FileNameAddAnchor = source.FileNameAddAnchor,
            FileNameAddContent = source.FileNameAddContent,
            FileNameAddAfterAnchor = source.FileNameAddAfterAnchor,

            FileNameDeleteByIndexEnabled = source.FileNameDeleteByIndexEnabled,
            FileNameDeleteStartIndex = source.FileNameDeleteStartIndex,
            FileNameDeleteCount = source.FileNameDeleteCount,

            FileNameDeleteByAnchorEnabled = source.FileNameDeleteByAnchorEnabled,
            FileNameDeleteAnchor = source.FileNameDeleteAnchor,
            FileNameDeleteAfterAnchor = source.FileNameDeleteAfterAnchor,
            FileNameDeleteAnchorCount = source.FileNameDeleteAnchorCount,

            SequenceTemplate = source.SequenceTemplate,
            SequenceStart = source.SequenceStart,
            SequenceStep = source.SequenceStep,
            SequenceDigits = source.SequenceDigits,
            SequencePadEnabled = source.SequencePadEnabled,
            SequenceAlphabetic = source.SequenceAlphabetic,
            SequenceUppercase = source.SequenceUppercase,
            SequenceValue = sequenceValue
        };
    }

    /// <summary>
    /// 渲染序号模板。占位符：
    /// <list type="bullet">
    /// <item><c>{name}</c> 原名称（不含扩展名）；</item>
    /// <item><c>{ext}</c> 文件类型（扩展名，不含点）；</item>
    /// <item><c>{date}</c> 修改日期（yyyy-MM-dd，未知时为空）；</item>
    /// <item><c>{n}</c> 序号（数字 / 字母由「字母编码」「字母大写」开关决定）；</item>
    /// <item><c>{a}</c> / <c>{A}</c> 英文小写 / 大写序号（不受开关影响，模板里写死）；</item>
    /// <item><c>#</c>（一个或多个）等价于 <c>{n}</c>（旧写法，继续支持）。</item>
    /// </list>
    /// 模板里没有 <c>{n}</c> 也没有 <c>#</c> 时，序号追加在模板末尾（旧语义，继续支持）。
    /// </summary>
    private static string BuildSequenceToken(int index, ProcessOptions options, string? nameCore = null, string? ext = null, string? dateText = null)
    {
        if (string.IsNullOrWhiteSpace(options.SequenceTemplate))
        {
            return string.Empty;
        }

        var value = options.SequenceStart + index * options.SequenceStep;
        var core = BuildSequenceCore(value, options.SequenceDigits, options.SequencePadEnabled, options.SequenceAlphabetic, options.SequenceUppercase);
        // {a}/{A} 是显式字母变量，不套「位数 / 补齐」—— 用户要的就是 a,b,c… / A,B,C…
        var alphaLower = BuildSequenceCore(value, 0, pad: false, alphabetic: true, uppercase: false);
        var alphaUpper = BuildSequenceCore(value, 0, pad: false, alphabetic: true, uppercase: true);

        var raw = options.SequenceTemplate;
        var hasNamedVariable =
            raw.Contains("{name}", StringComparison.Ordinal) ||
            raw.Contains("{ext}", StringComparison.Ordinal) ||
            raw.Contains("{date}", StringComparison.Ordinal) ||
            raw.Contains("{a}", StringComparison.Ordinal) ||
            raw.Contains("{A}", StringComparison.Ordinal);

        var template = raw
            .Replace("{name}", nameCore ?? string.Empty, StringComparison.Ordinal)
            .Replace("{ext}", ext ?? string.Empty, StringComparison.Ordinal)
            .Replace("{date}", dateText ?? string.Empty, StringComparison.Ordinal)
            .Replace("{a}", alphaLower, StringComparison.Ordinal)
            .Replace("{A}", alphaUpper, StringComparison.Ordinal);

        if (template.Contains("{n}", StringComparison.Ordinal))
        {
            return template.Replace("{n}", core, StringComparison.Ordinal);
        }

        if (template.Contains('#'))
        {
            return System.Text.RegularExpressions.Regex.Replace(template, "#+", core);
        }

        // 旧兼容：模板是纯文字（一个变量都没有）时，序号追加在末尾。
        // 模板里已经有 {name}/{date}/… 之类变量时绝不能再追加 —— 否则 "video_{date}" 会变成 "video_2026-10-0401"。
        return hasNamedVariable ? template : template + core;
    }

    private static string BuildSequenceCore(int value, int digits, bool pad, bool alphabetic, bool uppercase)
    {
        if (alphabetic)
        {
            var letters = NumberToLetters(value <= 0 ? 1 : value);
            var normalized = uppercase ? letters : letters.ToLowerInvariant();
            if (pad && digits > normalized.Length)
            {
                var fill = uppercase ? 'A' : 'a';
                normalized = new string(fill, digits - normalized.Length) + normalized;
            }

            return normalized;
        }

        var number = value.ToString();
        return pad && digits > 0 ? number.PadLeft(digits, '0') : number;
    }

    private static string NumberToLetters(int number)
    {
        var n = Math.Max(1, number);
        var chars = new Stack<char>();
        while (n > 0)
        {
            n--;
            chars.Push((char)('A' + (n % 26)));
            n /= 26;
        }

        return new string(chars.ToArray());
    }

    private void SaveSequenceTemplate()
    {
        var template = SequenceTemplate?.Trim();
        if (string.IsNullOrWhiteSpace(template)) return;

        if (!SequenceTemplatePresets.Contains(template, StringComparer.Ordinal))
        {
            SequenceTemplatePresets.Add(template);
            SaveSequenceTemplatePresets();
        }

        SelectedSequencePreset = template;
    }

    public void SaveSequenceTemplatePresets()
    {
        try
        {
            var dir = Path.GetDirectoryName(_sequencePresetFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var presets = SequenceTemplatePresets
                .Where(x => !string.IsNullOrWhiteSpace(x)
                            && !string.Equals(x, "自定义（在模板中使用 {n}）", StringComparison.Ordinal)
                            && !BuiltInSequencePresetDisplayNames.Contains(x, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var json = JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_sequencePresetFilePath, json, Encoding.UTF8);
        }
        catch
        {
            // ignore persist errors
        }
    }

    /// <summary>
    /// 把旧位置里的 json 数据「补缺」到当前主目录。
    /// <para>
    /// 升级不丢数据的第二道保险：凭据有自己的多目录搜索（见 <c>N115CredentialStore</c>），
    /// 但序号预设这类只从主目录读的文件，要靠这里把它们从旧位置搬过来。
    /// </para>
    /// <para>
    /// 用「缺失才复制」而不是「覆盖」：多个位置都有一份时，主目录那份（也就是用户正在用的）
    /// 优先，不会被旧位置的副本盖掉。
    /// </para>
    /// </summary>
    private void MigrateLegacyData()
    {
        try
        {
            Directory.CreateDirectory(DataDir);

            foreach (var sourceDir in AppDataPaths.ReadDirectories)
            {
                // 主目录自己跳过（它就是目标）；其余候选（exe 同级 / 每用户 / 历史遗留）都看一看
                if (string.Equals(sourceDir, DataDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Directory.Exists(sourceDir)) continue;

                foreach (var file in Directory.GetFiles(sourceDir, "*.json"))
                {
                    var dest = Path.Combine(DataDir, Path.GetFileName(file));
                    if (!File.Exists(dest))
                    {
                        File.Copy(file, dest);
                    }
                }
            }
        }
        catch
        {
            // ignore migration errors
        }
    }

    private void LoadSequenceTemplatePresets()
    {
        try
        {
            if (!File.Exists(_sequencePresetFilePath)) return;
            var json = File.ReadAllText(_sequencePresetFilePath, Encoding.UTF8);
            var presets = JsonSerializer.Deserialize<List<string>>(json);
            if (presets is null) return;

            foreach (var preset in presets)
            {
                if (string.IsNullOrWhiteSpace(preset)) continue;
                if (SequenceTemplatePresets.Contains(preset, StringComparer.Ordinal)) continue;
                SequenceTemplatePresets.Add(preset);
            }
        }
        catch
        {
            // ignore load errors
        }
    }

    private void UndoSequence()
    {
        if (TryUndoLastRenameBatch()) return;
        if (_sequenceUndoStack.Count == 0) return;
        var state = _sequenceUndoStack.Pop();
        _isApplyingUndo = true;
        try
        {
            SequenceTemplate = state.Template;
            SequenceStart = state.Start;
            SequenceStep = state.Step;
            SequenceDigits = state.Digits;
            SequencePadEnabled = state.Pad;
            SequenceAlphabetic = state.Alphabetic;
            SequenceUppercase = state.Uppercase;
        }
        finally
        {
            _isApplyingUndo = false;
        }

        UndoSequenceCommand.RaiseCanExecuteChanged();
        SaveSequenceTemplateCommand.RaiseCanExecuteChanged();
    }

    private void UndoAdd()
    {
        if (TryUndoLastRenameBatch()) return;
        if (_addUndoStack.Count == 0) return;
        var state = _addUndoStack.Pop();
        _isApplyingUndo = true;
        try
        {
            FileNamePrefixAdd = state.Prefix;
            FileNameSuffixAdd = state.Suffix;
            FileNameAddAnchor = state.Anchor;
            FileNameAddContent = state.Content;
            FileNameAddAfterAnchor = state.After;
        }
        finally
        {
            _isApplyingUndo = false;
        }

        UndoAddCommand.RaiseCanExecuteChanged();
    }

    private void UndoDelete()
    {
        if (TryUndoLastRenameBatch()) return;
        if (_deleteUndoStack.Count == 0) return;
        var state = _deleteUndoStack.Pop();
        _isApplyingUndo = true;
        try
        {
            FileNameDeleteAnchor = state.Anchor;
            FileNameDeleteByIndexEnabled = state.ByIndexEnabled;
            FileNameDeleteStartIndex = state.StartIndex;
            FileNameDeleteCount = state.Count;
            FileNameDeleteByAnchorEnabled = state.ByAnchorEnabled;
            FileNameDeleteAfterAnchor = state.AfterAnchor;
            FileNameDeleteAnchorCount = state.AnchorCount;
        }
        finally
        {
            _isApplyingUndo = false;
        }

        UndoDeleteCommand.RaiseCanExecuteChanged();
    }

    private bool TryUndoLastRenameBatch()
    {
        if (_lastRenameRollbackItems.Count == 0) return false;

        var ok = 0;
        var fail = 0;
        foreach (var item in _lastRenameRollbackItems.AsEnumerable().Reverse())
        {
            try
            {
                if (!File.Exists(item.CurrentPath))
                {
                    fail++;
                    continue;
                }

                var target = item.OriginalPath;
                if (File.Exists(target))
                {
                    target = NextAvailablePath(target);
                }

                File.Move(item.CurrentPath, target);
                var vm = Files.FirstOrDefault(f => string.Equals(f.FilePath, item.CurrentPath, StringComparison.OrdinalIgnoreCase));
                if (vm is not null)
                {
                    vm.FilePath = target;
                }
                ok++;
            }
            catch
            {
                fail++;
            }
        }

        _lastRenameRollbackItems.Clear();
        UndoSequenceCommand.RaiseCanExecuteChanged();
        UndoAddCommand.RaiseCanExecuteChanged();
        UndoDeleteCommand.RaiseCanExecuteChanged();

        UiDialog.Show($"已撤销重命名：成功 {ok}，失败 {fail}", "撤销", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private void PushSequenceUndoIfNeeded<T>(T current, T next)
    {
        if (_isApplyingUndo || EqualityComparer<T>.Default.Equals(current, next)) return;
        _sequenceUndoStack.Push(new SequenceUndoState(SequenceTemplate, SequenceStart, SequenceStep, SequenceDigits, SequencePadEnabled, SequenceAlphabetic, SequenceUppercase));
    }

    private void PushAddUndoIfNeeded<T>(T current, T next)
    {
        if (_isApplyingUndo || EqualityComparer<T>.Default.Equals(current, next)) return;
        _addUndoStack.Push(new AddUndoState(FileNamePrefixAdd, FileNameSuffixAdd, FileNameAddAnchor, FileNameAddContent, FileNameAddAfterAnchor));
    }

    private void PushDeleteUndoIfNeeded<T>(T current, T next)
    {
        if (_isApplyingUndo || EqualityComparer<T>.Default.Equals(current, next)) return;
        _deleteUndoStack.Push(new DeleteUndoState(FileNameDeleteAnchor, FileNameDeleteByIndexEnabled, FileNameDeleteStartIndex, FileNameDeleteCount, FileNameDeleteByAnchorEnabled, FileNameDeleteAfterAnchor, FileNameDeleteAnchorCount));
    }

    private sealed record SequenceUndoState(string Template, string Start, string Step, string Digits, bool Pad, bool Alphabetic, bool Uppercase);
    private sealed record AddUndoState(string Prefix, string Suffix, string Anchor, string Content, bool After);
    private sealed record DeleteUndoState(string Anchor, bool ByIndexEnabled, string StartIndex, string Count, bool ByAnchorEnabled, bool AfterAnchor, string AnchorCount);
    private sealed record RenameRollbackItem(string CurrentPath, string OriginalPath);

    private void SortFiles() => ReorderFiles(refreshPreviews: false);

    /// <summary>
    /// 点「原文件名」列头排序：**点一次降序、再点升序**（与 115 列表规则一致）。
    /// 返回切换后是否为降序，供界面在列头上画箭头。
    /// </summary>
    public bool SortFilesByName()
    {
        // 首次点这一列就给降序（与 115 列表一致：点一次降序、再点升序）；
        // 之后再点就来回切。
        _filesSortDescending = !_filesSortedByName || !_filesSortDescending;
        _filesSortedByName = true;

        // 顺序变了，预览里的序号（001_、002_…）也得跟着重算，
        // 否则用户看到的编号会和眼前的行序对不上。
        ReorderFiles(refreshPreviews: true);

        Log($"[列表] 排序：{DescribeFileSort()}（{Files.Count} 个文件）");
        return _filesSortDescending;
    }

    /// <summary>当前本地列表排序的中文描述，用于日志。</summary>
    public string DescribeFileSort()
        => _filesSortedByName
            ? (_filesSortDescending ? "按原文件名降序" : "按原文件名升序")
            : "按路径";

    /// <summary>
    /// 按 <paramref name="descending"/> 算出「自然排序原文件名」的顺序
    /// （数字段按数值比：新建文本文档.txt → 副本 → 副本 (2) → … → 副本 (15)）。
    /// 纯函数、不碰集合，便于离线断言。同名（不同目录）时用完整路径兜底，保证顺序稳定。
    /// </summary>
    public static List<FileItemViewModel> ArrangeFilesByName(
        IEnumerable<FileItemViewModel> files, bool descending)
    {
        var ordered = files.ToList();
        ordered.Sort((a, b) =>
        {
            var result = FileNameComparer.Compare(a.OriginalFileName, b.OriginalFileName);
            if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(a.FilePath, b.FilePath);
            return descending ? -result : result;
        });

        return ordered;
    }

    private void ReorderFiles(bool refreshPreviews)
    {
        var ordered = _filesSortedByName
            ? ArrangeFilesByName(Files, _filesSortDescending)
            : Files.OrderBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase).ToList();

        ApplyOrder(ordered);

        if (refreshPreviews) RefreshPreviewForAllFiles();
    }

    /// <summary>
    /// 把 <see cref="Files"/> 调成 <paramref name="ordered"/> 的顺序。
    /// 用 <see cref="ObservableCollection{T}.Move"/> 而不是 Clear + Add：
    /// 「选中文件」是双向绑在 DataGrid 的 SelectedItem 上的，Clear 会让界面把选中项清空
    /// （115 列表重建 Items 时踩过同一个坑），Move 则能保住当前选中行。
    /// </summary>
    private void ApplyOrder(IReadOnlyList<FileItemViewModel> ordered)
    {
        for (var i = 0; i < ordered.Count && i < Files.Count; i++)
        {
            var target = ordered[i];
            if (ReferenceEquals(Files[i], target)) continue;

            var from = Files.IndexOf(target);
            if (from >= 0 && from != i) Files.Move(from, i);
        }
    }

    private void RemoveSelectedFile()
    {
        if (SelectedFile is null) return;
        Files.Remove(SelectedFile);
        SelectedFile = null;
        RaiseCommandsCanExecute();
    }

    /// <summary>
    /// 「移除」按钮 / Delete 键：把列表中选中的文件（**支持 Ctrl / Shift 多选**）从列表里移除。
    /// 只移出列表，**绝不删除文件本体**。
    /// </summary>
    /// <returns>实际移除的个数（0 = 没有可移除的选中项）。</returns>
    public int RemoveFiles(System.Collections.IList? items)
    {
        if (items is null || items.Count == 0) return 0;

        var targets = items.OfType<FileItemViewModel>().Where(Files.Contains).ToList();
        foreach (var item in targets)
        {
            Files.Remove(item);
        }

        if (targets.Count > 0)
        {
            Log($"移除 {targets.Count} 个文件出列表（文件本体保留在磁盘）：{string.Join("、", targets.Select(x => x.OriginalFileName))}");
            SelectedFile = null;
            RaiseCommandsCanExecute();
        }

        return targets.Count;
    }

    /// <summary>
    /// 列表内拖动排序：把一组文件整体移动到目标插入位置（**保持组内相对顺序**）。
    /// insertIndex 是「移除前」的绝对位置：拖到目标行上半 = 插它前面，下半 = 插它后面，
    /// 拖到空白处 = <c>Files.Count</c>（追加到末尾）。
    /// 顺序变化经 <see cref="Files"/> 的 CollectionChanged 自动刷新范围提示与预览序号。
    /// </summary>
    public void MoveFilesTo(System.Collections.Generic.IReadOnlyList<FileItemViewModel> items, int insertIndex)
    {
        if (items is null || items.Count == 0) return;

        // 组内按当前列表顺序排好，整组搬移后相对顺序不变
        var dragged = items.Where(Files.Contains).OrderBy(Files.IndexOf).ToList();
        if (dragged.Count == 0) return;
        if (dragged.Count == Files.Count) return;   // 整个列表一起拖 = 没有可换的位置

        // 先按「移除前」的索引算修正量：被拖项里排在插入点之前的每有一项，插入点左移一格
        var beforeCount = dragged.Count(i => Files.IndexOf(i) < insertIndex);

        foreach (var item in dragged)
        {
            Files.Remove(item);
        }

        var target = Math.Clamp(insertIndex - beforeCount, 0, Files.Count);
        for (var i = 0; i < dragged.Count; i++)
        {
            Files.Insert(target + i, dragged[i]);
        }
    }

    /// <summary>
    /// 显式进入编辑模式（双击列表行 / 点状态列的编辑图标）。
    /// 2026-10-05 起选中行**不再**自动进编辑器，只有这条路径会切换到编辑器。
    /// </summary>
    public void OpenInEditor(FileItemViewModel? item)
    {
        if (item is null || !item.IsTextFile)
        {
            if (item is not null) EditorStatusText = "该文件是二进制内容，没有可编辑的文本";
            return;
        }

        if (!ReferenceEquals(SelectedFile, item))
        {
            SelectedFile = item;
        }

        CurrentMode = DisplayMode.Editor;
        item.Mode = DisplayMode.Editor;
    }

    private void RemoveSelectedRule()
    {
        if (Rules.Count <= 1 || SelectedRule is null) return;

        var idx = Rules.IndexOf(SelectedRule);
        if (idx < 0) return;

        Rules.RemoveAt(idx);
        if (Rules.Count == 0)
        {
            var newRule = new ReplaceRuleViewModel();
            Rules.Add(newRule);
            SelectedRule = newRule;
        }
        else
        {
            var nextIndex = Math.Min(idx, Rules.Count - 1);
            SelectedRule = Rules[nextIndex];
        }

        RaiseCommandsCanExecute();
    }

    private void RaiseCommandsCanExecute()
    {
        RunCommand.RaiseCanExecuteChanged();
        RunReplaceOnlyCommand.RaiseCanExecuteChanged();
        RunLanguageOnlyCommand.RaiseCanExecuteChanged();
        RunToSimplifiedCommand.RaiseCanExecuteChanged();
        SaveSubtitleCommand.RaiseCanExecuteChanged();
        ClearFilesCommand.RaiseCanExecuteChanged();
        RemoveSelectedFileCommand.RaiseCanExecuteChanged();
        CheckAllFilesCommand.RaiseCanExecuteChanged();
        UncheckAllFilesCommand.RaiseCanExecuteChanged();
        RemoveRuleCommand.RaiseCanExecuteChanged();
        UndoSequenceCommand.RaiseCanExecuteChanged();
        UndoAddCommand.RaiseCanExecuteChanged();
        UndoDeleteCommand.RaiseCanExecuteChanged();
    }

    private List<ReplaceRule> BuildRules(out string? error)
    {
        var result = new List<ReplaceRule>();
        error = null;
        for (var i = 0; i < Rules.Count; i++)
        {
            var find = Rules[i].FindText?.TrimEnd('\r', '\n') ?? string.Empty;
            var repl = Rules[i].ReplaceText?.TrimEnd('\r', '\n') ?? string.Empty;

            if (find.Length == 0 && repl.Length == 0) continue;
            if (find.Length == 0)
            {
                error = $"第 {i + 1} 条规则：查找不能为空";
                return [];
            }

            result.Add(new ReplaceRule { FindText = find, ReplaceText = repl });
        }

        return result;
    }

    /// <summary>「开始处理 / 批量处理 / 删除字幕外语 / 繁=&gt;简」四个入口共用同一条流水线，差别只在开关。</summary>
    private enum RunMode
    {
        /// <summary>开始处理：替换 + 删除行 + 删除语言 + 文件名操作。</summary>
        Full,

        /// <summary>各规则组里的「开始」：只做替换与文件名操作（不删行、不删语言）。</summary>
        ReplaceAndFileName,

        /// <summary>「删除字幕外语」区里的「开始处理」：只按勾选的语言过滤正文并写回，不改名、不替换。</summary>
        LanguageOnly,

        /// <summary>
        /// 列表工具条上的「繁=&gt;简」：文件名与字幕正文一起转简体，**不套用替换 / 序号等任何规则**。
        /// 与 <see cref="LanguageOnly"/> 的区别：**非字幕文件也要改名**（只是正文不读不写）。
        /// </summary>
        ToSimplifiedOnly,
    }

    private void Run(bool fullProcess) => RunCore(fullProcess ? RunMode.Full : RunMode.ReplaceAndFileName);

    /// <summary>「删除字幕外语」→ 开始处理。</summary>
    private void RunLanguageOnly() => RunCore(RunMode.LanguageOnly);

    /// <summary>本地列表工具条 →「繁=&gt;简」。</summary>
    private void RunToSimplified() => RunCore(RunMode.ToSimplifiedOnly);

    private void RunCore(RunMode mode)
    {
        var fullProcess = mode == RunMode.Full;
        var languageOnly = mode == RunMode.LanguageOnly;
        var toSimplifiedOnly = mode == RunMode.ToSimplifiedOnly;

        // 「删除字幕外语」「繁=>简」都不碰替换规则与文件名操作，这两块直接当没配：
        // 参数填错也不该拦住这两个按钮（它们本来就与规则无关）
        var ignoreRules = languageOnly || toSimplifiedOnly;

        string? ruleError = null;
        var rules = ignoreRules ? [] : BuildRules(out ruleError);
        if (!ignoreRules && ruleError is not null)
        {
            UiDialog.Show(ruleError, "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var deleteEnabled = fullProcess && DeleteLinesEnabled;
        var rmEn = (fullProcess || languageOnly) && RemoveEnglish;
        var rmJp = (fullProcess || languageOnly) && RemoveJapanese;
        var rmKr = (fullProcess || languageOnly) && RemoveKorean;

        if (languageOnly && !rmEn && !rmJp && !rmKr)
        {
            UiDialog.Show("请先在「删除字幕外语」里勾选要删除的语言（英文 / 日文 / 韩文），再点「开始处理」",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (deleteEnabled && (!int.TryParse(DeleteStartLine, out var s) || !int.TryParse(DeleteEndLine, out var e) || s <= 0 || e <= 0 || e < s))
        {
            UiDialog.Show("删除行范围无效：起始/结束行需为正整数，且结束行 >= 起始行", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var fileNameAnchorAddConfigured = !string.IsNullOrWhiteSpace(FileNameAddAnchor) && !string.IsNullOrEmpty(FileNameAddContent);
        var fileNamePrefixSuffixConfigured = !string.IsNullOrEmpty(FileNamePrefixAdd) || !string.IsNullOrEmpty(FileNameSuffixAdd);
        var sequenceConfigured = !string.IsNullOrWhiteSpace(SequenceTemplate);

        if (!ignoreRules && !TryValidateFileNameOps(out var fileNameOpsError))
        {
            UiDialog.Show(fileNameOpsError, "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var fileNameOpsEnabled = !ignoreRules &&
            (fileNamePrefixSuffixConfigured || fileNameAnchorAddConfigured || FileNameDeleteByIndexEnabled || FileNameDeleteByAnchorEnabled || sequenceConfigured);

        if (fullProcess && rules.Count == 0 && !deleteEnabled && !rmEn && !rmJp && !rmKr && !fileNameOpsEnabled)
        {
            UiDialog.Show("当前未启用任何处理（替换为空、删除行未启用、语言删除未选择）", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (mode == RunMode.ReplaceAndFileName && rules.Count == 0 && !fileNameOpsEnabled)
        {
            UiDialog.Show("请先填写要替换的内容或启用文件名添加/删除", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 处理范围 = 列表里勾选的文件（默认全选）。
        // 一个都没勾选时兜底为「全部」——与范围条上写明的「将处理全部 N 个文件」一致，不会悄悄什么都不做。
        var targets = ResolveRenameTargets(Files);

        // 2026-10-06 起不再弹「确认继续？」——直接执行，完成后弹「成功 / 失败数量」报告框。
        // 执行前的计划要点照旧写日志（不依赖确认框留痕）。

        var options = toSimplifiedOnly
            ? BuildToSimplifiedOptions(true, MakeBackup, null)
            : languageOnly
            ? BuildLanguageOnlyOptions(rmEn, rmJp, rmKr, true, MakeBackup, null)
            : new ProcessOptions
            {
                Rules = rules,
                DeleteLinesEnabled = deleteEnabled,
                DeleteStartLine = int.TryParse(DeleteStartLine, out var ds) ? ds : 0,
                DeleteEndLine = int.TryParse(DeleteEndLine, out var de) ? de : 0,
                RemoveEnglish = rmEn,
                RemoveJapanese = rmJp,
                RemoveKorean = rmKr,
                InPlace = true,
                MakeBackup = MakeBackup,
                OutputDirectory = null,

                FileNameAddEnabled = fileNameAnchorAddConfigured,
                FileNamePrefixAdd = FileNamePrefixAdd,
                FileNameSuffixAdd = FileNameSuffixAdd,
                FileNameAddAnchor = FileNameAddAnchor,
                FileNameAddContent = FileNameAddContent,
                FileNameAddAfterAnchor = FileNameAddAfterAnchor,

                FileNameDeleteByIndexEnabled = FileNameDeleteByIndexEnabled,
                FileNameDeleteStartIndex = int.TryParse(FileNameDeleteStartIndex, out var fileNameDeleteStartIndex) ? fileNameDeleteStartIndex : 0,
                FileNameDeleteCount = int.TryParse(FileNameDeleteCount, out var fileNameDeleteCount) ? fileNameDeleteCount : 0,

                FileNameDeleteByAnchorEnabled = FileNameDeleteByAnchorEnabled,
                FileNameDeleteAnchor = FileNameDeleteAnchor,
                FileNameDeleteAfterAnchor = FileNameDeleteAfterAnchor,
                FileNameDeleteAnchorCount = int.TryParse(FileNameDeleteAnchorCount, out var fileNameDeleteAnchorCount) ? fileNameDeleteAnchorCount : 0,

                SequenceTemplate = SequenceTemplate,
                SequenceStart = int.TryParse(SequenceStart, out var sequenceStart) ? sequenceStart : 0,
                SequenceStep = int.TryParse(SequenceStep, out var sequenceStep) ? sequenceStep : 1,
                SequenceDigits = int.TryParse(SequenceDigits, out var sequenceDigits) ? sequenceDigits : 2,
                SequencePadEnabled = SequencePadEnabled,
                SequenceAlphabetic = SequenceAlphabetic,
                SequenceUppercase = SequenceUppercase,
                SequenceValue = string.Empty
            };

        Log(new string('=', 60));
        // 措辞要点（2026-10-05）：标明「本地列表」—— 网盘改名走 N115ViewModel 自己的流程
        //（确认框写「将对 115 网盘中的 N 个文件改名」，日志带 [115] 前缀），别让用户把两边搞混
        Log(mode switch
        {
            RunMode.LanguageOnly => $"删除字幕外语（本地列表）：{targets.Count} 个文件（列表共 {Files.Count} 个，未勾选的已跳过）",
            RunMode.ToSimplifiedOnly => $"繁=>简（本地列表）：{targets.Count} 个文件（列表共 {Files.Count} 个，未勾选的已跳过）",
            RunMode.Full => $"开始处理（本地列表）：{targets.Count} 个文件（列表共 {Files.Count} 个，未勾选的已跳过）",
            _ => $"仅批量替换（本地列表）：{targets.Count} 个文件（列表共 {Files.Count} 个，未勾选的已跳过）",
        });
        Log($"替换：{(rules.Count > 0 ? $"已启用（{rules.Count}条规则）" : "未启用")}");
        Log($"文件名添加/删除：{(fileNameOpsEnabled ? "已启用" : "未启用")}");
        if (sequenceConfigured && !ignoreRules)
        {
            Log($"批量序号：模板={SequenceTemplate}, 开始={SequenceStart}, 增量={SequenceStep}, 位数={SequenceDigits}");
        }
        Log($"删除行：{(deleteEnabled ? $"已启用（{options.DeleteStartLine}-{options.DeleteEndLine}）" : "未启用")}");
        Log($"删除字幕外语：{(rmEn || rmJp || rmKr ? DescribeLanguageSelection(rmEn, rmJp, rmKr) : "未启用")}");
        if (options.ToSimplified)
        {
            Log("繁=>简：已启用（文件名 + 字幕正文）");
        }
        Log("输出：覆盖原文件（写回前按备份开关留 .bak）");

        var (ok, fail, skipped) = ProcessTargets(targets, options, rules, deleteEnabled, languageOnly);

        // 繁=>简 一个文件都不跳过（非字幕文件也会被改名），所以不给「跳过」的提示，免得误导
        var skippedNote = skipped > 0 ? $"，跳过 {skipped}（非字幕 / 文本）" : string.Empty;
        var summary = $"完成：成功 {ok}，失败 {fail}{skippedNote}";
        if (fail > 0)
        {
            LogError(summary);
        }
        else
        {
            Log(summary);
        }
        UiDialog.Show($"处理完成：成功 {ok}，失败 {fail}{skippedNote}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        UndoSequenceCommand.RaiseCanExecuteChanged();
        UndoAddCommand.RaiseCanExecuteChanged();
        UndoDeleteCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 构造「删除字幕外语 → 开始处理」用的处理选项：**只带语言开关**，
    /// 替换规则、文件名添加/删除、批量序号一律留空 —— 这个按钮的语义就是「只删语言、不改名」。
    /// 抽成纯函数，离线校验可以直接断言它的每一项。
    /// </summary>
    public static ProcessOptions BuildLanguageOnlyOptions(
        bool rmEn, bool rmJp, bool rmKr, bool inPlace, bool makeBackup, string? outputDirectory) => new()
    {
        Rules = [],
        DeleteLinesEnabled = false,
        RemoveEnglish = rmEn,
        RemoveJapanese = rmJp,
        RemoveKorean = rmKr,
        InPlace = inPlace,
        MakeBackup = makeBackup,
        OutputDirectory = outputDirectory,

        FileNameAddEnabled = false,
        FileNamePrefixAdd = string.Empty,
        FileNameSuffixAdd = string.Empty,
        FileNameAddAnchor = string.Empty,
        FileNameAddContent = string.Empty,

        FileNameDeleteByIndexEnabled = false,
        FileNameDeleteByAnchorEnabled = false,

        SequenceTemplate = string.Empty,
        SequenceStart = 0,
        SequenceStep = 1,
        SequenceDigits = 2,
        SequenceValue = string.Empty,
    };

    /// <summary>
    /// 构造「繁=&gt;简」按钮用的处理选项：**只有 ToSimplified 这一个开关**，
    /// 替换规则、删除行、语言过滤、文件名添加/删除、批量序号一律留空 ——
    /// 这个按钮的语义就是「把繁体转成简体」，与右侧功能栏的任何规则无关。
    /// 抽成纯函数，离线校验可以直接断言它的每一项。
    /// </summary>
    public static ProcessOptions BuildToSimplifiedOptions(
        bool inPlace, bool makeBackup, string? outputDirectory) => new()
    {
        Rules = [],
        DeleteLinesEnabled = false,
        RemoveEnglish = false,
        RemoveJapanese = false,
        RemoveKorean = false,
        ToSimplified = true,
        InPlace = inPlace,
        MakeBackup = makeBackup,
        OutputDirectory = outputDirectory,

        FileNameAddEnabled = false,
        FileNamePrefixAdd = string.Empty,
        FileNameSuffixAdd = string.Empty,
        FileNameAddAnchor = string.Empty,
        FileNameAddContent = string.Empty,

        FileNameDeleteByIndexEnabled = false,
        FileNameDeleteByAnchorEnabled = false,

        SequenceTemplate = string.Empty,
        SequenceStart = 0,
        SequenceStep = 1,
        SequenceDigits = 2,
        SequenceValue = string.Empty,
    };

    /// <summary>
    /// 流水线的**无弹窗内核**：逐个处理给定文件并返回统计（成功 / 失败 / 跳过）。
    /// 确认框、参数校验、日志抬头都在 <see cref="RunCore"/> 里；这里只干活。
    /// 单独抽出来的原因：离线校验需要一个能在无窗口环境真实跑一遍的入口
    /// （<c>MessageBox</c> 在控制台进程里会直接把断言卡死）。
    /// </summary>
    public (int Ok, int Fail, int Skipped) ProcessTargets(
        IReadOnlyList<FileItemViewModel> targets, ProcessOptions options, List<ReplaceRule> rules, bool deleteEnabled, bool languageOnly)
    {
        var ok = 0;
        var fail = 0;
        var skipped = 0;
        _lastRenameRollbackItems.Clear();

        for (var i = 0; i < targets.Count; i++)
        {
            var file = targets[i];

            // 只读条目（拖入时无写入权限）：内容写不了、改名也可能失败，整条跳过
            if (file.IsReadOnly)
            {
                skipped++;
                file.Preview = "只读，已跳过";
                Log($"[SKIP] {file.OriginalFileName} - 只读（无写入权限），不参与处理");
                continue;
            }

            // 语言删除只对字幕 / 文本类有意义：其余文件原样跳过，一个字节都不动
            if (languageOnly && !file.IsTextFile)
            {
                skipped++;
                Log($"[SKIP] {file.OriginalFileName} - 非字幕 / 文本文件，不参与语言删除");
                continue;
            }

            // 只碰勾选的文件：未勾选的原样不动（内容、文件名都不改）
            file.Preview = "处理中...";
            file.IsProcessing = true;
            file.IsSuccess = null;

            // 模板变量（{name} / {ext} / {date}）需要每个文件自己的信息；
            // 修改日期读磁盘拿不到就留空（{date} 渲染成空串，不至于整段模板失败）
            var stem0 = Path.GetFileNameWithoutExtension(file.OriginalFileName);
            var ext0 = Path.GetExtension(file.OriginalFileName).TrimStart('.');
            var sequenceValue = BuildSequenceToken(i, options, stem0, ext0, ResolveLocalDateText(file.FilePath));
            var fileOptions = BuildOptionsWithSequence(options, sequenceValue);

            // 逐文件 INFO：处理前先记录「第几个 / 原名 / 计划写入的序号值」，
            // 失败时配合 ERROR 行能完整还原当时算出的序号（排障关键）
            Log($"({i + 1}/{targets.Count}) 处理 {file.OriginalFileName}" +
                (sequenceValue.Length > 0 ? $"（序号值={sequenceValue}）" : string.Empty));

            var res = file.IsTextFile
                ? ProcessTextFileFromMemory(file, fileOptions, rules, deleteEnabled)
                : ProcessFileByRenameOnly(file, fileOptions);

            file.Preview = res.Message;
            file.IsProcessing = false;
            file.IsSuccess = res.Success;
            if (res.Success && !string.IsNullOrWhiteSpace(res.NewFilePath))
            {
                file.FilePath = res.NewFilePath;
            }

            if (res.Success)
            {
                ok++;
                Log($"[OK] ({i + 1}/{targets.Count}) {file.OriginalFileName} → {res.Message}");
                Services.FileLogSink.Debug($"[本地] ({i + 1}/{targets.Count}) {file.OriginalFileName} 处理返回：Success=true, Message={res.Message}"
                    + (res.NewFilePath is null ? "" : $", NewFilePath={res.NewFilePath}"));
            }
            else
            {
                fail++;
                LogError($"[FAIL] ({i + 1}/{targets.Count}) {file.OriginalFileName} - {res.Message}");
                Services.FileLogSink.Debug($"[本地] ({i + 1}/{targets.Count}) {file.OriginalFileName} 处理返回：Success=false, Message={res.Message}");
            }
        }

        return (ok, fail, skipped);
    }

    /// <summary>
    /// 本地文件的修改日期（yyyy-MM-dd），给序号模板的 <c>{date}</c> 用。
    /// 拿不到（文件不存在 / 时钟异常）返回 null —— <c>{date}</c> 渲染为空。
    /// </summary>
    private static string? ResolveLocalDateText(string? filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
            var t = File.GetLastWriteTime(filePath);
            return t.Year >= 1980 ? t.ToString("yyyy-MM-dd") : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把当前勾选的语言整理成给用户看的文案（确认框与日志共用）。</summary>
    private static string DescribeLanguageSelection(bool rmEn, bool rmJp, bool rmKr)
    {
        var names = new List<string>();
        if (rmEn) names.Add("英文 EN");
        if (rmJp) names.Add("日文 JP");
        if (rmKr) names.Add("韩文 KR");
        return string.Join(" + ", names);
    }

    /// <summary>
    /// 非文本类文件（任意扩展名，含无扩展名）：只按文件名处理 —— 改名（覆盖模式）
    /// 或改名后复制到输出目录，**不读取正文**。字幕 / 文本类走 <see cref="ProcessTextFileFromMemory"/>。
    /// </summary>
    private ProcessResult ProcessFileByRenameOnly(FileItemViewModel file, ProcessOptions options)
    {
        try
        {
            var fileInfo = new FileInfo(file.FilePath);
            if (!fileInfo.Exists)
            {
                return new ProcessResult { Success = false, Message = "文件不存在" };
            }

            var (stem, ext) = TextBatchProcessor.ApplyFileNameRules(fileInfo.Name, options, isDirectory: false);

            // 繁=>简 放在最后：用户填的规则多半是照着「原文件名（繁体）」写的，
            // 先让规则匹配上，最后再统一把主干里的繁体转成简体。
            if (options.ToSimplified)
            {
                stem = ChineseConverter.ToSimplified(stem);
            }

            if (string.IsNullOrWhiteSpace(stem))
            {
                return new ProcessResult { Success = false, Message = "替换后文件名为空，已跳过" };
            }

            var newName = stem + ext;
            if (options.InPlace)
            {
                var targetPath = Path.Combine(fileInfo.DirectoryName!, newName);
                if (string.Equals(targetPath, fileInfo.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    // 措辞要点（2026-10-05）：写明「新旧名字相同」，避免误解为「执行了改名但没生效」
                    return new ProcessResult { Success = true, Message = $"新名与当前名相同（{newName}），无需修改" };
                }

                targetPath = NextAvailablePath(targetPath);
                File.Move(fileInfo.FullName, targetPath);
                _lastRenameRollbackItems.Add(new RenameRollbackItem(targetPath, fileInfo.FullName));
                return new ProcessResult { Success = true, Message = $"已重命名为：{Path.GetFileName(targetPath)}", NewFilePath = targetPath };
            }

            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            {
                return new ProcessResult { Success = false, Message = "未指定输出目录" };
            }

            Directory.CreateDirectory(options.OutputDirectory);
            var outPath = NextAvailablePath(Path.Combine(options.OutputDirectory, newName));
            File.Copy(fileInfo.FullName, outPath);
            return new ProcessResult { Success = true, Message = $"已复制输出：{Path.GetFileName(outPath)}" };
        }
        catch (Exception ex)
        {
            return new ProcessResult { Success = false, Message = $"处理失败：{ex.Message}" };
        }
    }

    /// <summary>
    /// 就地保存的「动作描述」（不含开头的「已」），由实际生效的开关拼出来（不再靠调用方传进来的模式标志猜）：
    /// 无开关 → 「覆盖保存」；只删语言 → 「删除字幕外语并保存」；只繁转简 → 「繁转简并保存」；两者都做则依次列出。
    /// </summary>
    private static string BuildInPlaceSavedAction(ProcessOptions options)
    {
        var notes = new List<string>();
        if (options.RemoveEnglish || options.RemoveJapanese || options.RemoveKorean) notes.Add("删除字幕外语");
        if (options.ToSimplified) notes.Add("繁转简");
        return notes.Count > 0 ? $"{string.Join("、", notes)}并保存" : "覆盖保存";
    }

    private ProcessResult ProcessTextFileFromMemory(FileItemViewModel file, ProcessOptions options, List<ReplaceRule> rules, bool deleteEnabled)
    {
        try
        {
            if (string.IsNullOrEmpty(file.Content))
            {
                // 空文件不再整体失败（2026-10-05）：正文没有内容可改写 —— 替换 / 删行 /
                // 删语言对空串的处理结果还是空串，写回等于没写；而批量序号、前后缀添加、
                // 删段这些**文件名操作与正文无关**，必须照常生效。直接走「仅改名」路径。
                return ProcessFileByRenameOnly(file, options);
            }

            // 用户手改过就以编辑器内容为准；没改过则回到原始正文按当前规则重算
            var baseContent = ResolveBaseContent(file);
            var content = baseContent;
            var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

            // 删除字幕外语：按当前勾选的语言过滤正文。
            // 放在最前面，「删除行范围」的行号才对得上编辑器里看到的行号；
            // 过滤本身是幂等的，对已过滤的内容再跑一次结果不变。
            if (options.RemoveEnglish || options.RemoveJapanese || options.RemoveKorean)
            {
                content = TextBatchProcessor.ProcessTextContent(
                    content, options.RemoveEnglish, options.RemoveJapanese, options.RemoveKorean, newline);
                content = content.TrimEnd('\r', '\n') + newline;
            }

            // Apply delete line range
            if (deleteEnabled)
            {
                var lines = content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).ToList();
                lines = DeleteLineRange(lines, options.DeleteStartLine, options.DeleteEndLine);
                content = string.Join(newline, lines).TrimEnd('\r', '\n') + newline;
            }

            // Apply replace rules
            if (rules.Count > 0)
            {
                content = TextBatchProcessor.ApplyReplaceRules(content, rules);
                content = content.TrimEnd('\r', '\n') + newline;
            }

            // 繁=>简 放在内容改写的最后一步：同样是为了让用户填的（繁体）替换规则先匹配上。
            // 转换是幂等的 —— 已经是简体就一个字符都不会变。
            if (options.ToSimplified)
            {
                content = ChineseConverter.ToSimplified(content);
            }

            // Determine target filename（options 已含规则与渲染好的序号模板）
            var (targetStem, targetExt) = TextBatchProcessor.ApplyFileNameRules(
                Path.GetFileName(file.FilePath), options, isDirectory: false);
            if (options.ToSimplified)
            {
                targetStem = ChineseConverter.ToSimplified(targetStem);
            }

            if (string.IsNullOrWhiteSpace(targetStem))
            {
                return new ProcessResult { Success = false, Message = "文件名处理后为空，已跳过" };
            }

            var targetName = targetStem + targetExt;

            // 繁=>简 是幂等的：内容与文件名都没变就什么都别写 ——
            // 否则再点一次按钮会平白生成一堆 .bak、还把文件时间戳全刷一遍。
            if (options.ToSimplified
                && string.Equals(content, baseContent, StringComparison.Ordinal)
                && string.Equals(targetName, Path.GetFileName(file.FilePath), StringComparison.Ordinal))
            {
                return new ProcessResult { Success = true, Message = "已是简体，无需修改" };
            }

            // Read original encoding for writing（优先拖入时判定的编码，避免重检测猜错）
            var encoding = file.CurrentEncoding
                ?? TextBatchProcessor.ReadTextWithEncodingFallback(file.FilePath).Encoding
                ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            if (options.InPlace)
            {
                if (options.MakeBackup)
                {
                    File.Copy(file.FilePath, NextAvailablePath(file.FilePath + ".bak"));
                }

                WriteAtomic(file.FilePath, content, encoding, newline);

                // 磁盘内容已经变了（语言行被删、替换生效…）：让编辑器与之一致，
                // 否则用户再点「保存」，会把刚删掉的内容又原样写回去。
                file.SetProgrammaticContent(content);
                file.SetOriginalContent(content);

                var renameTarget = Path.Combine(Path.GetDirectoryName(file.FilePath)!, targetName);
                if (!string.Equals(renameTarget, file.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    renameTarget = NextAvailablePath(renameTarget);
                    File.Move(file.FilePath, renameTarget);
                    _lastRenameRollbackItems.Add(new RenameRollbackItem(renameTarget, file.FilePath));

                    // 顺手改名时也要说清正文做了什么（否则「繁=>简」的结果看起来只是改了个名）
                    var action = BuildInPlaceSavedAction(options);
                    var renameNote = action == "覆盖保存" ? string.Empty : $"（{action}）";
                    return new ProcessResult
                    {
                        Success = true,
                        Message = $"已覆盖并重命名：{Path.GetFileName(renameTarget)}{renameNote}",
                        NewFilePath = renameTarget
                    };
                }

                return new ProcessResult { Success = true, Message = $"已{BuildInPlaceSavedAction(options)}" };
            }

            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            {
                return new ProcessResult { Success = false, Message = "未指定输出目录" };
            }

            Directory.CreateDirectory(options.OutputDirectory);
            var outPath = NextAvailablePath(Path.Combine(options.OutputDirectory, targetName));
            WriteAtomic(outPath, content, encoding, newline);
            return new ProcessResult { Success = true, Message = $"已输出：{Path.GetFileName(outPath)}" };
        }
        catch (Exception ex)
        {
            return new ProcessResult { Success = false, Message = $"处理失败：{ex.Message}" };
        }
    }

    private static List<string> DeleteLineRange(List<string> lines, int startLine, int endLine)
    {
        if (startLine <= 0 || endLine <= 0 || endLine < startLine)
        {
            return lines;
        }

        var start = startLine - 1;
        var end = endLine - 1;
        if (start >= lines.Count)
        {
            return lines;
        }

        end = Math.Min(end, lines.Count - 1);
        lines.RemoveRange(start, end - start + 1);
        return lines;
    }

    private static void WriteAtomic(string path, string content, Encoding encoding, string newline)
    {
        _ = newline; // content is already normalized upstream

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp_" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tmp, content, encoding);
            ReplaceWithRetry(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    /// <summary>
    /// 落盘替换带短暂重试：刚写完的文件常被杀软实时扫描短暂独占（无共享打开），
    /// File.Replace / File.Move 会随机抛 IOException —— 保存「偶发失败」就是这么来的。
    /// 重试 5 次（50ms 递增）足以躲过扫描窗口；仍失败就把异常抛给调用方正常报错。
    /// </summary>
    private static void ReplaceWithRetry(string tmp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(target))
                {
                    File.Replace(tmp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, target);
                }

                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static string NextAvailablePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    public void Log(string message)
    {
        // 日志窗口每条带时间戳（用户要求，2026-10-06；同年 10-06 起去掉年份只显示 MM-dd HH:mm:ss）；
        // 落盘文件由 FileLogSink 自带完整时间戳 + 级别，所以那里仍传原始 message，避免一行出现两个时间戳
        Logs.Add($"[{DateTime.Now:MM-dd HH:mm:ss}] {message}");
        FileLogSink.Write(message);
    }

    /// <summary>
    /// ERROR 级日志：任何操作失败 / 异常都走这里 —— 内存日志窗口与普通日志同显，
    /// 落盘文件里这行会带 <c>[ERROR]</c> 前缀，方便在 5MB 滚动的大文件里直接筛错误。
    /// </summary>
    public void LogError(string message)
    {
        Logs.Add($"[{DateTime.Now:MM-dd HH:mm:ss}] [ERROR] {message}");
        FileLogSink.Write(message, "ERROR");
    }

    private void AttachRulePreviewRefresh(ReplaceRuleViewModel rule)
    {
        rule.PropertyChanged += RuleOnPropertyChanged;
    }

    private void RuleOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReplaceRuleViewModel.FindText) or nameof(ReplaceRuleViewModel.ReplaceText))
        {
            RefreshPreviewForAllFiles();
        }
    }

    /// <summary>
    /// 重算本地列表的「预览」列。
    /// <para>
    /// 序号按「在**勾选集合**里的位置」算 —— 必须与 <see cref="Run"/> 真正执行时用的下标一致：
    /// 只勾选一部分时，预览里的编号就该是这一部分内部的编号，而不是整张表的行号。
    /// 未勾选的行**不显示预览**（格子留空、<c>PreviewInScope=false</c>，悬停说明原因），
    /// 免得改一下规则这些行的预览也跟着变，看起来像它们也要被处理。
    /// </para>
    /// </summary>
    private void RefreshPreviewForAllFiles()
    {
        var scope = new Dictionary<FileItemViewModel, int>();
        var fallback = new Dictionary<FileItemViewModel, int>();

        foreach (var file in Files)
        {
            fallback[file] = fallback.Count;              // 整表序号（一个都没勾选时用）
            if (file.IsChecked) scope[file] = scope.Count; // 勾选集合序号（真正会用的编号）
        }

        var hasScope = scope.Count > 0;

        foreach (var file in Files)
        {
            if (file.IsProcessing)
            {
                continue;
            }

            // 一个都没勾选时不做范围过滤：Run 的兜底规则就是「没勾选 ⇒ 处理全部」
            var inScope = !hasScope || scope.ContainsKey(file);
            file.PreviewInScope = inScope;

            file.Preview = inScope
                ? BuildPreviewText(file, scope.TryGetValue(file, out var scopedIndex) ? scopedIndex : fallback[file])
                : string.Empty;
        }

        // 115 列表共用右侧功能栏的同一套规则，「预览」列要跟着规则一起变
        N115.RefreshPreview();
    }

    private string BuildPreviewText(FileItemViewModel file, int index)
    {
        var (newName, _) = BuildRenamedName(file.OriginalFileName, index, dateText: ResolveLocalDateText(file.FilePath));
        return string.IsNullOrWhiteSpace(newName) ? "预览结果为空" : newName;
    }

    /// <summary>
    /// 算出「开始处理 / 批量处理」这次要动哪些文件：**只取勾选的**，未勾选的一点都不碰；
    /// 一个都没勾选时回退为全部（范围条上会写「将处理全部 N 个文件」，不会让人意外）。
    /// 返回顺序 = 列表顺序，也就是预览里序号所依据的顺序。
    /// 纯函数，便于离线断言。
    /// </summary>
    public static List<FileItemViewModel> ResolveRenameTargets(IReadOnlyList<FileItemViewModel> files)
    {
        var targets = files.Where(f => f.IsChecked).ToList();
        return targets.Count > 0 ? targets : [.. files];
    }

    /// <summary>
    /// 按右侧功能栏当前配置计算新文件名（含扩展名）。本地文件与 115 网盘文件共用这一套逻辑，
    /// 保证「预览」和实际改名结果一致，且两端行为不漂移（实际变换统一走
    /// <see cref="TextBatchProcessor.ApplyFileNameTransform"/>，这里只负责准备输入）。
    /// <paramref name="isDirectory"/> 为 true 时按「文件夹名」处理：整名就是主体，不拆扩展名
    /// （否则「2024.01 素材」这类带点的文件夹名会被误切成主干 + 伪扩展名）。
    /// <paramref name="dateText"/> 是文件的修改日期（yyyy-MM-dd），给模板变量 <c>{date}</c> 用；
    /// 网盘条目由调用方从接口返回的时间换算，本地文件在调用点读磁盘。未知传 null（<c>{date}</c> 渲染为空）。
    /// Error 非空表示参数不合法；NewName 为空字符串表示处理结果为空。
    /// </summary>
    public (string NewName, string? Error) BuildRenamedName(
        string originalFileName, int index, bool isDirectory = false, string? dateText = null)
    {
        if (!TryValidateFileNameOps(out var validationError))
        {
            return (originalFileName, validationError);
        }

        var rules = BuildRules(out var ruleError);
        if (ruleError is not null)
        {
            return (originalFileName, ruleError);
        }

        // 规则替换走统一入口：规则引用 {后缀名…} 时作用于完整文件名（可改扩展名），否则只作用于主干
        var rulesOptions = CreateFileNameOptions(rules: rules);
        var (stem, ext) = TextBatchProcessor.ApplyFileNameRules(originalFileName, rulesOptions, isDirectory, applyTransform: false);

        var sequenceValue = BuildSequenceToken(index, rulesOptions, stem, ext.TrimStart('.'), dateText);

        var nameOptions = CreateFileNameOptions(sequenceValue);
        var newStem = TextBatchProcessor.ApplyFileNameTransform(stem, nameOptions);

        return string.IsNullOrWhiteSpace(newStem)
            ? (string.Empty, "处理后文件名为空")
            : (newStem + ext, null);
    }

    /// <summary>只填充文件名相关字段的选项（内容处理相关字段一律关闭）。
    /// 文件名添加 / 删除字段取右侧功能栏的**真实值** —— 本地改名、115 网盘改名与预览共用。
    /// <paramref name="sequenceValue"/> 是已渲染好的序号模板结果（<see cref="SequenceValue"/> 是 init-only，
    /// 只能在初始化器里赋值，所以做成参数而不是事后改）。
    /// <paramref name="rules"/> 用于预览 / 网盘路径把替换规则一并塞进选项（ProcessOptions 全 init-only）。</summary>
    private ProcessOptions CreateFileNameOptions(string sequenceValue = "", List<ReplaceRule>? rules = null)
    {
        return new ProcessOptions
        {
            Rules = rules ?? [],
            DeleteLinesEnabled = false,
            DeleteStartLine = 0,
            DeleteEndLine = 0,
            RemoveEnglish = false,
            RemoveJapanese = false,
            RemoveKorean = false,
            InPlace = true,
            MakeBackup = false,
            OutputDirectory = null,
            FileNameAddEnabled = FileNameAddEnabled,
            FileNamePrefixAdd = FileNamePrefixAdd,
            FileNameSuffixAdd = FileNameSuffixAdd,
            FileNameAddAnchor = FileNameAddAnchor,
            FileNameAddContent = FileNameAddContent,
            FileNameAddAfterAnchor = FileNameAddAfterAnchor,
            FileNameDeleteByIndexEnabled = FileNameDeleteByIndexEnabled,
            FileNameDeleteStartIndex = int.TryParse(FileNameDeleteStartIndex, out var delStart) ? delStart : 0,
            FileNameDeleteCount = int.TryParse(FileNameDeleteCount, out var delCount) ? delCount : 0,
            FileNameDeleteByAnchorEnabled = FileNameDeleteByAnchorEnabled,
            FileNameDeleteAnchor = FileNameDeleteAnchor,
            FileNameDeleteAfterAnchor = FileNameDeleteAfterAnchor,
            FileNameDeleteAnchorCount = int.TryParse(FileNameDeleteAnchorCount, out var anchorCount) ? anchorCount : 0,
            SequenceTemplate = SequenceTemplate,
            SequenceStart = int.TryParse(SequenceStart, out var seqStart) ? seqStart : 1,
            SequenceStep = int.TryParse(SequenceStep, out var seqStep) ? seqStep : 1,
            SequenceDigits = int.TryParse(SequenceDigits, out var seqDigits) ? seqDigits : 2,
            SequencePadEnabled = SequencePadEnabled,
            SequenceAlphabetic = SequenceAlphabetic,
            SequenceUppercase = SequenceUppercase,
            SequenceValue = sequenceValue
        };
    }

    /// <summary>校验右侧「文件名添加/删除」与「批量序号」的参数，本地处理与 115 网盘改名共用。</summary>
    private bool TryValidateFileNameOps(out string message)
    {
        message = string.Empty;

        if (FileNameDeleteByIndexEnabled)
        {
            if (!int.TryParse(FileNameDeleteStartIndex, out var deleteStartIndex) || deleteStartIndex <= 0)
            {
                message = "文件名删除（按字符位置）：起始字符必须为正整数";
                return false;
            }

            if (!int.TryParse(FileNameDeleteCount, out var deleteCount) || deleteCount <= 0)
            {
                message = "文件名删除（按字符位置）：删除个数必须为正整数";
                return false;
            }
        }

        if (FileNameDeleteByAnchorEnabled)
        {
            if (string.IsNullOrWhiteSpace(FileNameDeleteAnchor))
            {
                message = "文件名删除（按定位字）：请填写定位字";
                return false;
            }

            if (!int.TryParse(FileNameDeleteAnchorCount, out var deleteByAnchorCount) || deleteByAnchorCount <= 0)
            {
                message = "文件名删除（按定位字）：删除个数必须为正整数";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(SequenceTemplate))
        {
            if (!int.TryParse(SequenceStart, out _))
            {
                message = "批量序号：开始于必须为整数";
                return false;
            }

            if (!int.TryParse(SequenceStep, out var seqStep) || seqStep <= 0)
            {
                message = "批量序号：增量必须为正整数";
                return false;
            }

            if (!int.TryParse(SequenceDigits, out var seqDigits) || seqDigits <= 0)
            {
                message = "批量序号：位数必须为正整数";
                return false;
            }
        }

        return true;
    }
}
