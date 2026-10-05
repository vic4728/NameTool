namespace NameTool.Models;

public sealed class ProcessOptions
{
    public required List<ReplaceRule> Rules { get; init; }
    public bool DeleteLinesEnabled { get; init; }
    public int DeleteStartLine { get; init; }
    public int DeleteEndLine { get; init; }
    public bool RemoveEnglish { get; init; }
    public bool RemoveJapanese { get; init; }
    public bool RemoveKorean { get; init; }

    /// <summary>「繁=>简」：把文件名与字幕正文里的繁体统一转成简体（幂等，已是简体则无变化）。</summary>
    public bool ToSimplified { get; init; }

    public bool InPlace { get; init; }
    public bool MakeBackup { get; init; }
    public string? OutputDirectory { get; init; }

    public bool FileNameAddEnabled { get; init; }
    public string FileNamePrefixAdd { get; init; } = string.Empty;
    public string FileNameSuffixAdd { get; init; } = string.Empty;
    public string FileNameAddAnchor { get; init; } = string.Empty;
    public string FileNameAddContent { get; init; } = string.Empty;
    public bool FileNameAddAfterAnchor { get; init; }

    public bool FileNameDeleteByIndexEnabled { get; init; }
    public int FileNameDeleteStartIndex { get; init; }
    public int FileNameDeleteCount { get; init; }

    public bool FileNameDeleteByAnchorEnabled { get; init; }
    public string FileNameDeleteAnchor { get; init; } = string.Empty;
    public bool FileNameDeleteAfterAnchor { get; init; }
    public int FileNameDeleteAnchorCount { get; init; }

    public string SequenceTemplate { get; init; } = string.Empty;
    public int SequenceStart { get; init; }
    public int SequenceStep { get; init; }
    public int SequenceDigits { get; init; }
    public bool SequencePadEnabled { get; init; }
    public bool SequenceAlphabetic { get; init; }
    public bool SequenceUppercase { get; init; }
    public string SequenceValue { get; init; } = string.Empty;
}

public sealed class ProcessResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public string? NewFilePath { get; init; }
}
