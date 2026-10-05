using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NameTool.Models;

namespace NameTool.Services;

public enum SubtitleFormat
{
    Srt,
    Ass,
    PlainText
}

public sealed class TextBatchProcessor
{
    /// <summary>
    /// **确定按文本处理**的扩展名（字幕 / 纯文本 / 常见文本格式）——这些直接进编辑器、读改正文。
    /// 其余扩展名（含无扩展名）不再一刀切当二进制：走 <see cref="IsTextFile"/> 做**内容嗅探**，
    /// 像文本（.lrc / 脚本 / 无扩展名的文本…）同样能进编辑模式；
    /// 只有嗅探出二进制特征（NUL 字节 / 控制字符占比过高）才仅改名，绝不读坏文件。
    /// </summary>
    public static readonly HashSet<string> TextExts =
    [
        ".srt", ".ass", ".txt",
        ".md", ".csv", ".json", ".yaml", ".yml",
        ".html", ".xml", ".log", ".php", ".bat", ".js", ".css", ".ini", ".nfo",
    ];

    /// <summary>常见音视频扩展名。**仅用于提示归类**，不再是处理白名单。</summary>
    public static readonly HashSet<string> MediaExts = [".mp3", ".mp4", ".mkv", ".mov", ".ts", ".wav"];

    /// <summary>
    /// 文件（或其所在目录）是否**可写**——拖入时判定是否要弹「只读模式」询问。
    /// 文件带只读属性 / 拒绝写访问、所在目录建不了临时文件，都算不可写；
    /// 文件只是被别的程序占用（IOException）不算没权限，仍按可写处理。
    /// </summary>
    public static bool CanWriteFile(string filePath)
    {
        try
        {
            if ((File.GetAttributes(filePath) & FileAttributes.ReadOnly) != 0) return false;
        }
        catch
        {
            return false;
        }

        try
        {
            using (new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
            }
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // 文件正被占用 ≠ 没有写权限，继续看目录
        }
        catch
        {
            return false;
        }

        return CanWriteDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)));
    }

    /// <summary>目录是否可写：试着建一个临时文件再删掉，能建能删就是可写。</summary>
    public static bool CanWriteDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        try
        {
            var probe = Path.Combine(dir, ".nt_wtest_" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 这个文件是否按文本处理：已知文本扩展名直接算；其余看内容嗅探。
    /// （替换 <see cref="FileItemViewModel.IsTextFile"/> 旧的「只认三个扩展名」规则。）
    /// </summary>
    public static bool IsTextFile(string filePath)
        => TextExts.Contains(Path.GetExtension(filePath).ToLowerInvariant())
           || LooksLikeTextFile(filePath);

    /// <summary>
    /// 内容嗅探：读头部 8KB 判断是否「像文本」。
    /// <list type="bullet">
    /// <item>含 NUL(0x00) 字节 ⇒ 二进制（绝大多数二进制格式都有）；</item>
    /// <item>控制字符（除 \t \r \n）占比 &gt; 5% ⇒ 二进制；</item>
    /// <item>空文件 / 读不出 ⇒ 按文本处理（空文件本来就能编辑）。</item>
    /// </list>
    /// ⚠️ 无 BOM 的 UTF-16 文本头部大量 0x00 会被误判成二进制 —— 极少见（Windows 文本默认带 BOM），
    /// 且旧规则下它同样不可编辑，不算回退。
    /// </summary>
    public static bool LooksLikeTextFile(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> buf = stackalloc byte[8192];
            var read = fs.Read(buf);
            if (read == 0) return true;                                  // 空文件按文本
            if (buf[..read].Contains((byte)0)) return false;             // 有 NUL ⇒ 二进制

            var control = 0;
            for (var i = 0; i < read; i++)
            {
                var b = buf[i];
                if (b < 32 && b != 9 && b != 10 && b != 13) control++;
            }

            return control * 100.0 / read <= 5.0;
        }
        catch
        {
            return false;   // 打不开 / IO 异常 ⇒ 不冒险按文本改写
        }
    }

    public static SubtitleFormat DetectSubtitleFormat(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return SubtitleFormat.PlainText;
        }

        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");

        // Check for ASS format (contains Dialogue: or Comment: lines)
        if (normalized.Contains("Dialogue:") || normalized.Contains("Comment:"))
        {
            return SubtitleFormat.Ass;
        }

        // Check for SRT format (contains lines with timestamp pattern like "00:00:00,000 --> 00:00:00,000")
        if (Regex.IsMatch(normalized, @"\d{1,2}:\d{2}:\d{2},\d{3}\s*-->\s*\d{1,2}:\d{2}:\d{2},\d{3}"))
        {
            return SubtitleFormat.Srt;
        }

        return SubtitleFormat.PlainText;
    }

    public static string ProcessTextContent(string content, bool removeEnglish, bool removeJapanese, bool removeKorean, string newline)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content ?? string.Empty;
        }

        if (!removeEnglish && !removeJapanese && !removeKorean)
        {
            return content;
        }

        var format = DetectSubtitleFormat(content);
        return format switch
        {
            SubtitleFormat.Srt => FilterSrtByLanguage(content, removeEnglish, removeJapanese, removeKorean, newline),
            SubtitleFormat.Ass => FilterAssByLanguage(content, removeEnglish, removeJapanese, removeKorean, newline),
            _ => FilterTxtByLanguage(content, removeEnglish, removeJapanese, removeKorean, newline)
        };
    }

    public static (string? Content, Encoding? Encoding, string? Newline) ReadTextFileContent(string filePath)
    {
        var (content, encoding) = ReadTextWithEncodingFallback(filePath);
        if (content is null || encoding is null)
        {
            return (null, null, null);
        }

        var newline = DetectNewline(content);
        return (content, encoding, newline);
    }

    public ProcessResult ProcessOne(string filePath, ProcessOptions options)
    {
        try
        {
            var file = new FileInfo(filePath);
            if (!file.Exists)
            {
                return new ProcessResult { Success = false, Message = "文件不存在" };
            }

            // 任意扩展名都受理：文本类（已知扩展名或内容嗅探像文本）改写正文，其余一律「仅改名 / 复制」。
            // 没有扩展名的文件同样按内容判断，不再拒绝。
            return IsTextFile(file.FullName)
                ? ProcessTextFile(file, options)
                : ProcessFileByRenameOnly(file, options);
        }
        catch (Exception ex)
        {
            return new ProcessResult { Success = false, Message = $"处理失败：{ex.Message}" };
        }
    }

    private ProcessResult ProcessFileByRenameOnly(FileInfo file, ProcessOptions options)
    {
        if (options.Rules.Count == 0 && !HasFileNameTransform(options))
        {
            return new ProcessResult { Success = false, Message = "该文件仅支持改名：请先配置替换规则或文件名添加/删除" };
        }

        var (stem, ext) = ApplyFileNameRules(file.Name, options, isDirectory: false);
        var newName = stem + ext;

        if (string.IsNullOrWhiteSpace(newName))
        {
            return new ProcessResult { Success = false, Message = "替换后文件名为空，已跳过" };
        }

        if (options.InPlace)
        {
            var targetPath = Path.Combine(file.DirectoryName!, newName);
            if (string.Equals(targetPath, file.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return new ProcessResult { Success = true, Message = "文件名无变化" };
            }

            targetPath = NextAvailablePath(targetPath);
            File.Move(file.FullName, targetPath);
            return new ProcessResult { Success = true, Message = $"已重命名为：{Path.GetFileName(targetPath)}" };
        }

        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            return new ProcessResult { Success = false, Message = "未指定输出目录" };
        }

        Directory.CreateDirectory(options.OutputDirectory);
        var outPath = NextAvailablePath(Path.Combine(options.OutputDirectory, newName));
        File.Copy(file.FullName, outPath);
        return new ProcessResult { Success = true, Message = $"已复制输出：{Path.GetFileName(outPath)}" };
    }

    private ProcessResult ProcessTextFile(FileInfo file, ProcessOptions options)
    {
        var (content, encoding) = ReadTextWithEncodingFallback(file.FullName);
        if (content is null || encoding is null)
        {
            return new ProcessResult { Success = false, Message = "无法读取编码" };
        }

        var newline = DetectNewline(content);
        var lines = content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).ToList();

        if (options.DeleteLinesEnabled)
        {
            lines = DeleteLineRange(lines, options.DeleteStartLine, options.DeleteEndLine);
        }

        var text = string.Join(newline, lines).TrimEnd('\r', '\n') + newline;

        if (options.RemoveEnglish || options.RemoveJapanese || options.RemoveKorean)
        {
            var ext = file.Extension.ToLowerInvariant();
            text = ext switch
            {
                ".srt" => FilterSrtByLanguage(text, options.RemoveEnglish, options.RemoveJapanese, options.RemoveKorean, newline),
                ".ass" => FilterAssByLanguage(text, options.RemoveEnglish, options.RemoveJapanese, options.RemoveKorean, newline),
                _ => FilterTxtByLanguage(text, options.RemoveEnglish, options.RemoveJapanese, options.RemoveKorean, newline)
            };
        }

        text = ApplyReplaceRules(text, options.Rules);
        text = text.TrimEnd('\r', '\n') + newline;

        var targetStem = ApplyFileNameTransform(Path.GetFileNameWithoutExtension(file.Name), options);
        if (string.IsNullOrWhiteSpace(targetStem))
        {
            return new ProcessResult { Success = false, Message = "文件名处理后为空，已跳过" };
        }

        var targetName = targetStem + file.Extension;

        if (options.InPlace)
        {
            if (options.MakeBackup)
            {
                var backup = NextAvailablePath(file.FullName + ".bak");
                File.Copy(file.FullName, backup);
            }

            WriteAtomic(file.FullName, text, encoding, newline);

            var renameTargetPath = Path.Combine(file.DirectoryName!, targetName);
            if (!string.Equals(renameTargetPath, file.FullName, StringComparison.OrdinalIgnoreCase))
            {
                renameTargetPath = NextAvailablePath(renameTargetPath);
                File.Move(file.FullName, renameTargetPath);
                return new ProcessResult { Success = true, Message = $"已覆盖并重命名：{Path.GetFileName(renameTargetPath)}（编码：{encoding.WebName}）" };
            }

            return new ProcessResult { Success = true, Message = $"已覆盖（编码：{encoding.WebName}）" };
        }

        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            return new ProcessResult { Success = false, Message = "未指定输出目录" };
        }

        Directory.CreateDirectory(options.OutputDirectory);
        var outPath = NextAvailablePath(Path.Combine(options.OutputDirectory, targetName));
        WriteAtomic(outPath, text, encoding, newline);
        return new ProcessResult { Success = true, Message = $"已输出：{Path.GetFileName(outPath)}" };
    }

    private static bool HasFileNameTransform(ProcessOptions options)
    {
        return !string.IsNullOrEmpty(options.FileNamePrefixAdd)
               || !string.IsNullOrEmpty(options.FileNameSuffixAdd)
               || options.FileNameAddEnabled
               || options.FileNameDeleteByIndexEnabled
               || options.FileNameDeleteByAnchorEnabled
               || !string.IsNullOrEmpty(options.SequenceValue);
    }

    /// <summary>
    /// 按文件名相关选项（前/后添加、定位添加、删除、序号模板）把原名主干变成新主干。
    /// 本地改名与 115 网盘改名共用这一份实现 —— 两端语义必须完全一致，别再复制副本。
    /// </summary>
    public static string ApplyFileNameTransform(string input, ProcessOptions options)
    {
        var output = input;

        if (!string.IsNullOrEmpty(options.FileNamePrefixAdd))
        {
            output = options.FileNamePrefixAdd + output;
        }

        if (!string.IsNullOrEmpty(options.FileNameSuffixAdd))
        {
            output += options.FileNameSuffixAdd;
        }

        if (options.FileNameAddEnabled &&
            !string.IsNullOrWhiteSpace(options.FileNameAddAnchor) &&
            !string.IsNullOrEmpty(options.FileNameAddContent))
        {
            var idx = output.IndexOf(options.FileNameAddAnchor, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var insertPos = options.FileNameAddAfterAnchor ? idx + options.FileNameAddAnchor.Length : idx;
                output = output.Insert(insertPos, options.FileNameAddContent);
            }
        }

        if (options.FileNameDeleteByIndexEnabled && options.FileNameDeleteStartIndex > 0 && options.FileNameDeleteCount > 0)
        {
            var start = options.FileNameDeleteStartIndex - 1;
            if (start < output.Length)
            {
                var count = Math.Min(options.FileNameDeleteCount, output.Length - start);
                output = output.Remove(start, count);
            }
        }

        if (options.FileNameDeleteByAnchorEnabled &&
            !string.IsNullOrWhiteSpace(options.FileNameDeleteAnchor) &&
            options.FileNameDeleteAnchorCount > 0)
        {
            var idx = output.IndexOf(options.FileNameDeleteAnchor, StringComparison.Ordinal);
            if (idx >= 0)
            {
                if (options.FileNameDeleteAfterAnchor)
                {
                    var start = idx + options.FileNameDeleteAnchor.Length;
                    if (start < output.Length)
                    {
                        var count = Math.Min(options.FileNameDeleteAnchorCount, output.Length - start);
                        output = output.Remove(start, count);
                    }
                }
                else
                {
                    var count = Math.Min(options.FileNameDeleteAnchorCount, idx);
                    if (count > 0)
                    {
                        var start = idx - count;
                        output = output.Remove(start, count);
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(options.SequenceValue))
        {
            // 序号模板作为名字主体。注意前/后添加**不能丢**（2026-10 用户反馈：网盘改名时
            // 「文件名前/后添加」只要配了序号模板就不生效）—— 套在模板结果的最外层。
            // 此处 output 上已经加过一遍前后缀（本函数开头），模板分支要整个重建，避免双倍。
            var core = options.SequenceValue;
            if (!string.IsNullOrEmpty(options.FileNamePrefixAdd))
            {
                core = options.FileNamePrefixAdd + core;
            }

            if (!string.IsNullOrEmpty(options.FileNameSuffixAdd))
            {
                core += options.FileNameSuffixAdd;
            }

            return core;
        }

        return output;
    }

    /// <summary>
    /// 读取文本文件并判定编码（2026-10-05 重写，修 GB2312 显示乱码）：
    /// 旧实现第一个就用**宽松 UTF-8** 整读——非法字节被悄悄替换成 U+FFFD（�）而不报错，
    /// GB2312/GBK 文件「成功」读出一串乱码，永远轮不到 GB 编码。
    /// 新顺序：BOM 直判 → **严格 UTF-8**（非法字节抛异常）→ gb18030（GB2312/GBK 超集）→ Big5 → Unicode 无 BOM → 1252 兜底。
    /// </summary>
    public static (string? Content, Encoding? Encoding) ReadTextWithEncodingFallback(string path)
    {
        try
        {
            // ① BOM 直判：有 BOM 就没有猜测空间
            var bom = DetectBomEncoding(path);
            if (bom is not null)
            {
                return (File.ReadAllText(path, bom), bom);
            }

            // ② 严格 UTF-8：invalid byte 直接抛异常，才轮得到下面的 GB 系
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            try
            {
                return (File.ReadAllText(path, strictUtf8), strictUtf8);
            }
            catch
            {
                // 不是合法 UTF-8 —— 走多字节中文编码
            }

            // ③ gb18030：GB2312 / GBK 的完整超集，且与它们字节兼容；解码几乎不失败
            var gb = Encoding.GetEncoding("gb18030");
            return (File.ReadAllText(path, gb), gb);
        }
        catch
        {
            // ④ 兜底：Big5 / UTF-16 无 BOM / 1252，逐个试，全失败才算读不出
            foreach (var enc in new[]
                     {
                         Encoding.GetEncoding("big5"),
                         Encoding.Unicode,
                         Encoding.GetEncoding(1252)
                     })
            {
                try
                {
                    return (File.ReadAllText(path, enc), enc);
                }
                catch
                {
                    // ignore
                }
            }
        }

        return (null, null);
    }

    /// <summary>按 BOM 判定编码（无 BOM 或读不出时返回 null，交给内容探测）。</summary>
    public static Encoding? DetectBomEncoding(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            var read = fs.Read(head);
            if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            }

            if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            {
                return new UnicodeEncoding(false, true); // UTF-16 LE（写回保留 BOM）
            }

            if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            {
                return new UnicodeEncoding(true, true);  // UTF-16 BE
            }
        }
        catch
        {
            // 文件读不了就让调用方走统一失败路径
        }

        return null;
    }

    /// <summary>
    /// 目标编码能否无损表示 <paramref name="text"/>（用 EncoderExceptionFallback 严格试编码）。
    /// 编码转换前用它拦截「GB2312 装不下生僻字」这类有损转换，避免静默写成 ? 号。
    /// </summary>
    public static bool CanEncodeAll(Encoding encoding, string text)
    {
        var strict = Encoding.GetEncoding(encoding.CodePage, new EncoderExceptionFallback(), new DecoderExceptionFallback());
        try
        {
            _ = strict.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static string DetectNewline(string content) => content.Contains("\r\n") ? "\r\n" : "\n";

    private static List<string> DeleteLineRange(List<string> lines, int startLine, int endLine)
    {
        if (startLine <= 0 || endLine <= 0 || endLine < startLine)
        {
            return lines;
        }

        var start = startLine - 1;
        if (start >= lines.Count)
        {
            return lines;
        }

        var count = Math.Min(endLine, lines.Count) - start;
        lines.RemoveRange(start, count);
        return lines;
    }

    private static void WriteAtomic(string path, string content, Encoding encoding, string newline)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = NextAvailablePath(path + ".tmp");
        using (var sw = new StreamWriter(tmp, false, encoding))
        {
            sw.NewLine = newline;
            sw.Write(content);
        }

        ReplaceWithRetry(tmp, path);
    }

    /// <summary>
    /// 落盘替换带短暂重试：刚写完的文件常被杀软实时扫描短暂独占（无共享打开），
    /// File.Move(overwrite) 会随机抛 IOException —— 处理「偶发失败」就是这么来的。
    /// 重试 5 次（50ms 递增）足以躲过扫描窗口；仍失败就把异常抛给调用方正常报错。
    /// </summary>
    private static void ReplaceWithRetry(string tmp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, target, true);
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
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name}.{i}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }

    private static bool ContainsHiraganaKatakana(string text)
        => text.Any(ch => (ch >= 0x3040 && ch <= 0x309F) || (ch >= 0x30A0 && ch <= 0x30FF));

    private static bool ContainsHangul(string text)
        => text.Any(ch => ch >= 0xAC00 && ch <= 0xD7AF);

    private static bool ContainsCjk(string text)
        => text.Any(ch => (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF));

    private static bool IsEnglishSubtitleLine(string text)
    {
        var s = text.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        if (ContainsCjk(s) || ContainsHiraganaKatakana(s) || ContainsHangul(s)) return false;
        if (!Regex.IsMatch(s, "[A-Za-z\\u00C0-\\u024F\\u1E00-\\u1EFF]")) return false;

        return Regex.IsMatch(s, "^[A-Za-z\\u00C0-\\u024F\\u1E00-\\u1EFF0-9\\s\\.,\\?!'\"\\(\\)\\-\\[\\]\\{\\}:;…&@#%^*+=/\\\\<>|~`$”“‘’—–]*$");
    }

    private static bool ShouldRemoveByLanguage(string text, bool rmEn, bool rmJp, bool rmKr)
    {
        if (rmJp && ContainsHiraganaKatakana(text)) return true;
        if (rmKr && ContainsHangul(text)) return true;
        if (rmEn && IsEnglishSubtitleLine(text)) return true;
        return false;
    }

    public static string FilterSrtByLanguage(string raw, bool rmEn, bool rmJp, bool rmKr, string newline)
    {
        var t = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        var blocks = t.Split("\n\n");
        var outBlocks = new List<string>();
        foreach (var block in blocks)
        {
            var bb = block.Trim('\n');
            if (string.IsNullOrWhiteSpace(bb)) continue;
            var lines = bb.Split('\n').ToList();
            if (lines.Count < 3)
            {
                outBlocks.Add(bb);
                continue;
            }

            var header = lines.Take(2).ToList();
            var content = lines.Skip(2)
                .Where(l => !ShouldRemoveByLanguage(l, rmEn, rmJp, rmKr))
                .ToList();

            if (content.Count > 0)
            {
                outBlocks.Add(string.Join("\n", header.Concat(content)));
            }
        }

        return (string.Join("\n\n", outBlocks).TrimEnd('\n') + "\n").Replace("\n", newline);
    }

    public static string FilterAssByLanguage(string raw, bool rmEn, bool rmJp, bool rmKr, string newline)
    {
        // 匹配 ASS 覆盖标签，如 {\rEng}、{\an8}、{\i1} 等
        var overrideTagPattern = new Regex(@"\{\\[^}]*\}", RegexOptions.Compiled);

        var lines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var output = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("Dialogue:", StringComparison.Ordinal) ||
                trimmed.StartsWith("Comment:", StringComparison.Ordinal))
            {
                var parts = line.Split(',', 10);
                if (parts.Length == 10)
                {
                    var textPart = parts[9];
                    var filtered = FilterAssTextSegments(textPart, rmEn, rmJp, rmKr, overrideTagPattern);
                    if (filtered is null)
                    {
                        // 所有段落均被删除，移除整行
                        continue;
                    }

                    parts[9] = filtered;
                    output.Add(string.Join(',', parts));
                    continue;
                }
            }

            output.Add(line);
        }

        return string.Join(newline, output).TrimEnd('\r', '\n') + newline;
    }

    /// <summary>
    /// 对 ASS Dialogue 文本按 \N 拆分段落，逐段判断语言并过滤。
    /// 覆盖标签仅用于语言检测时剥离，保留的段落原文输出（标签完整保留）。
    /// 返回 null 表示所有段落均被删除（整行应移除）。
    /// </summary>
    private static string? FilterAssTextSegments(string text, bool rmEn, bool rmJp, bool rmKr, Regex overrideTagPattern)
    {
        if (!rmEn && !rmJp && !rmKr) return text;

        // ASS 中 \N 是软换行（同一对话内的行断），\n 是硬换行
        var segments = text.Split("\\N", StringSplitOptions.None);
        var kept = new List<string>();

        foreach (var segment in segments)
        {
            // 去除 ASS 覆盖标签后判断语言（仅用于检测，不修改原文）
            var clean = overrideTagPattern.Replace(segment, "").Trim();

            if (string.IsNullOrEmpty(clean))
            {
                // 纯标签段落（如 {\rEng}），随英文段落一起删除
                continue;
            }

            if (ShouldRemoveByLanguage(clean, rmEn, rmJp, rmKr))
            {
                // 纯外文段落（含其前面的语言切换标签），整段删除
                continue;
            }

            // 保留原文，覆盖标签完整保留（如 {\an3\fs30\fad(2800,1000)}）
            kept.Add(segment);
        }

        if (kept.Count == 0) return null;

        return string.Join("\\N", kept);
    }

    public static string FilterTxtByLanguage(string raw, bool rmEn, bool rmJp, bool rmKr, string newline)
    {
        var lines = raw.Replace("\r\n", "\n").Replace("\r", "\n")
            .Split('\n')
            .Where(l => !ShouldRemoveByLanguage(l, rmEn, rmJp, rmKr));
        return string.Join(newline, lines).TrimEnd('\r', '\n') + newline;
    }

    public static string ApplyReplaceRules(string input, IEnumerable<ReplaceRule> rules)
    {
        var output = input;
        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(rule.FindText)) continue;
            output = ApplyWildcardReplace(output, rule.FindText, rule.ReplaceText);
        }

        return output;
    }

    /// <summary>
    /// 规则的查找或替换侧是否引用了 <c>{后缀名…}</c> 变量。
    /// 引用时规则需要作用于「含扩展名的完整文件名」（可连扩展名一起改），否则只作用于主干（旧语义）。
    /// </summary>
    public static bool RulesTouchExtension(IEnumerable<ReplaceRule> rules)
    {
        return rules.Any(r =>
            (r.FindText?.Contains("{后缀名", StringComparison.Ordinal) ?? false)
            || (r.ReplaceText?.Contains("{后缀名", StringComparison.Ordinal) ?? false));
    }

    /// <summary>
    /// 文件名改名统一入口（本地与 115 网盘共用，两端语义必须一致，别再复制副本）：
    /// ① 按替换规则改名 —— 规则引用 <c>{后缀名…}</c> 时作用于完整文件名（可改扩展名），
    ///    否则只作用于主干（旧语义，扩展名原样保留）；
    /// ② 按文件名变换选项处理（前/后添加、定位添加/删除、序号模板 <see cref="ProcessOptions.SequenceValue"/>）。
    /// 返回 (主干, 含点扩展名)；<paramref name="applyTransform"/> 为 false 时跳过 ②
    /// （<see cref="MainViewModel.BuildRenamedName"/> 要先拿主干/扩展名渲染序号模板变量时用）。
    /// </summary>
    public static (string Stem, string Extension) ApplyFileNameRules(
        string originalFileName, ProcessOptions options, bool isDirectory, bool applyTransform = true)
    {
        if (isDirectory)
        {
            // 文件夹整名作主体，不拆扩展名（否则「2024.01 素材」会被误切成主干 + 伪扩展名）
            var dirStem = options.Rules.Count > 0
                ? ApplyReplaceRules(originalFileName, options.Rules)
                : originalFileName;
            return (applyTransform ? ApplyFileNameTransform(dirStem, options) : dirStem, string.Empty);
        }

        var ext = Path.GetExtension(originalFileName);
        var stem = Path.GetFileNameWithoutExtension(originalFileName);

        if (options.Rules.Count > 0)
        {
            if (RulesTouchExtension(options.Rules))
            {
                // 规则显式引用 {后缀名…}：直接在完整文件名上处理（可改扩展名）
                foreach (var rule in options.Rules)
                {
                    if (string.IsNullOrEmpty(rule.FindText)) continue;
                    var full = stem + ext;
                    var newFull = ApplyRuleToFullName(full, rule);
                    if (string.Equals(newFull, full, StringComparison.Ordinal)) continue;
                    ext = Path.GetExtension(newFull);
                    stem = ext.Length > 0 ? newFull[..^ext.Length] : newFull;
                }
            }
            else
            {
                foreach (var rule in options.Rules)
                {
                    if (string.IsNullOrEmpty(rule.FindText)) continue;
                    var newStem = ApplyWildcardReplace(stem, rule.FindText, rule.ReplaceText);
                    if (!string.Equals(newStem, stem, StringComparison.Ordinal))
                    {
                        stem = newStem;
                        continue;
                    }

                    // 主干没命中：含通配符/变量的规则再试完整文件名 —— 用户常把「.xxx」当名字的
                    // 一部分（如「Ab.cd001」，真扩展名判定会把 .cd001 划给扩展名，主干里没内容可匹配）。
                    // 回退由 ApplyRuleToFullName 把关：命中段必须跨过扩展名分界点，避免误伤 mp4 这类扩展名。
                    var full = stem + ext;
                    var newFull = ApplyRuleToFullName(full, rule);
                    if (!string.Equals(newFull, full, StringComparison.Ordinal))
                    {
                        ext = Path.GetExtension(newFull);
                        stem = ext.Length > 0 ? newFull[..^ext.Length] : newFull;
                    }
                }
            }
        }

        if (applyTransform)
        {
            stem = ApplyFileNameTransform(stem, options);
        }

        return (stem, ext);
    }

    private static string ApplyWildcardReplace(string text, string findPattern, string replaceText)
    {
        var translation = TranslatePattern(findPattern);
        if (translation is null)
        {
            return text.Replace(findPattern, replaceText, StringComparison.Ordinal);
        }

        return translation.Regex.Replace(text, m => RenderMatch(translation, replaceText, m));
    }

    /// <summary>按替换文本渲染单个匹配：命名变量 / 旧版 #、{*} 位置引用 / 纯字面量。</summary>
    private static string RenderMatch(PatternTranslation translation, string replaceText, Match m)
    {
        var tokens = ParseReplacementTokens(replaceText, translation, out var hasNamed);
        if (hasNamed)
        {
            return RenderReplacement(tokens, m);
        }

        var hasCaptureRef = Regex.IsMatch(replaceText, "#+|\\{\\*\\}");
        if (!hasCaptureRef)
        {
            return replaceText;
        }

        // 旧版位置引用：第 N 个 # / {*} 对应第 N 个捕获组
        var groupNum = 0;
        var sb = new StringBuilder();
        for (var i = 0; i < replaceText.Length;)
        {
            if (replaceText[i] == '#')
            {
                while (i < replaceText.Length && replaceText[i] == '#') i++;
                groupNum++;
                sb.Append($"${groupNum}");
                continue;
            }

            if (i + 2 < replaceText.Length && replaceText[i] == '{' && replaceText[i + 1] == '*' && replaceText[i + 2] == '}')
            {
                groupNum++;
                sb.Append($"${groupNum}");
                i += 3;
                continue;
            }

            sb.Append(replaceText[i]);
            i++;
        }

        return m.Result(sb.ToString());
    }

    /// <summary>
    /// 把单条规则应用到「完整文件名」上，返回新全名（无命中返回原文）。
    /// 只替换**跨过「.扩展名」分界点**的那一次命中（没有这样的命中时，仅当规则引用了
    /// <c>{后缀名…}</c> 才退而取第一次命中 —— 支持「{后缀名=MKV} 只写在替换侧补扩展名」的用法），
    /// 绝不全文替换 —— 否则 mp4 / h264 这类「字母+数字」扩展名会被同一条正则误伤。
    /// </summary>
    private static string ApplyRuleToFullName(string full, ReplaceRule rule)
    {
        var find = rule.FindText;
        var lastDot = full.LastIndexOf('.');
        var touchesExt = RulesTouchExtension([rule]);

        var translation = TranslatePattern(find);
        if (translation is null)
        {
            // 字面查找：只有命中段覆盖分界点才回退到全名
            var idx = full.IndexOf(find, StringComparison.Ordinal);
            if (idx < 0 || lastDot < 0 || !(idx <= lastDot && idx + find.Length > lastDot)) return full;
            return full[..idx] + rule.ReplaceText + full[(idx + find.Length)..];
        }

        Match? target = null;
        foreach (Match m in translation.Regex.Matches(full))
        {
            if (m.Index <= lastDot && m.Index + m.Length > lastDot && m.Length > 0)
            {
                target = m;
                break;
            }
        }

        target ??= touchesExt && lastDot >= 0 && translation.Regex.Matches(full).Count > 0
            ? translation.Regex.Matches(full)[0]
            : null;

        if (target is null) return full;

        return full[..target.Index] + RenderMatch(translation, rule.ReplaceText, target) + full[(target.Index + target.Length)..];
    }

    /// <summary>查找侧可识别的变量名（长的在前，避免「英文」抢先匹配「大写英文」的前缀）。</summary>
    private static readonly string[] VariableNames = ["大写英文", "小写英文", "后缀名", "英文", "数字", "中文"];

    /// <summary>
    /// 解析 <c>{变量名[序号][=值]}</c>。返回 false 表示不是变量（按字面量处理）。
    /// 空值（<c>{英文=}</c>）与无值等价。
    /// </summary>
    private static bool TryParseVariable(string text, int start, out string name, out int? index, out string? value, out int length)
    {
        name = string.Empty;
        index = null;
        value = null;
        length = 0;

        if (start >= text.Length || text[start] != '{') return false;
        var end = text.IndexOf('}', start);
        if (end < 0) return false;

        var inner = text[(start + 1)..end];
        var eq = inner.IndexOf('=');
        var head = eq >= 0 ? inner[..eq] : inner;
        var rawValue = eq >= 0 ? inner[(eq + 1)..] : null;
        value = string.IsNullOrEmpty(rawValue) ? null : rawValue;

        // 结尾数字是序号：{英文2}
        var k = head.Length;
        while (k > 0 && char.IsDigit(head[k - 1])) k--;
        if (k < head.Length)
        {
            if (int.TryParse(head[k..], out var n) && n > 0) index = n;
            head = head[..k];
        }

        foreach (var candidate in VariableNames)
        {
            if (string.Equals(head, candidate, StringComparison.Ordinal))
            {
                name = candidate;
                length = end - start + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>查找模式翻译结果：正则 + 各类变量的捕获组编号（按出现顺序）。</summary>
    private sealed class PatternTranslation
    {
        public Regex Regex = null!;
        public List<int> EnglishGroups { get; } = [];
        public List<int> DigitGroups { get; } = [];
        public List<int> ChineseGroups { get; } = [];
        public List<int> ExtGroups { get; } = [];
    }

    /// <summary>
    /// 把查找模式翻成正则。识别旧通配符（# → 数字、* → 任意、? → 单字符）与
    /// 中文变量：<c>{英文}</c>、<c>{大写英文}</c>、<c>{小写英文}</c>、<c>{数字}</c>、<c>{中文}</c>（各捕获一段）、
    /// <c>{后缀名=xxx}</c>（匹配「.xxx」，忽略大小写；无值时匹配任意字母数字扩展名）。
    /// </summary>
    private static PatternTranslation? TranslatePattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;

        var sb = new StringBuilder();
        var translation = new PatternTranslation();
        var groupNum = 0;
        var sawToken = false;

        for (var i = 0; i < pattern.Length;)
        {
            var ch = pattern[i];

            if (ch == '{' && TryParseVariable(pattern, i, out var name, out _, out var value, out var len))
            {
                sawToken = true;
                i += len;

                if (value is not null)
                {
                    // 查找侧带值：{英文=ED} 等按字面量匹配；{后缀名=mp4} 匹配「.mp4」且忽略大小写
                    if (name == "后缀名")
                    {
                        groupNum++;
                        sb.Append("\\.((?i:").Append(Regex.Escape(value)).Append("))");
                        translation.ExtGroups.Add(groupNum);
                    }
                    else
                    {
                        sb.Append(Regex.Escape(value));
                    }

                    continue;
                }

                groupNum++;
                switch (name)
                {
                    case "英文":
                        sb.Append("([A-Za-z]+)");
                        translation.EnglishGroups.Add(groupNum);
                        break;
                    case "大写英文":
                        sb.Append("([A-Z]+)");
                        translation.EnglishGroups.Add(groupNum);
                        break;
                    case "小写英文":
                        sb.Append("([a-z]+)");
                        translation.EnglishGroups.Add(groupNum);
                        break;
                    case "数字":
                        sb.Append("(\\d+)");
                        translation.DigitGroups.Add(groupNum);
                        break;
                    case "中文":
                        sb.Append("([\\u4e00-\\u9fff]+)");
                        translation.ChineseGroups.Add(groupNum);
                        break;
                    case "后缀名":
                        sb.Append("\\.([A-Za-z0-9]*)");
                        translation.ExtGroups.Add(groupNum);
                        break;
                }

                continue;
            }

            if (ch == '#')
            {
                while (i < pattern.Length && pattern[i] == '#') i++;
                groupNum++;
                sb.Append("(\\d+)");
                sawToken = true;
                continue;
            }

            if (ch == '*')
            {
                sb.Append("(.*)");
                groupNum++;
                sawToken = true;
                i++;
                continue;
            }

            if (ch == '?')
            {
                sb.Append("(.)");
                groupNum++;
                sawToken = true;
                i++;
                continue;
            }

            if (ch == '\\' && i + 1 < pattern.Length)
            {
                sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                i += 2;
                continue;
            }

            sb.Append(Regex.Escape(ch.ToString()));
            i++;
        }

        if (!sawToken) return null;

        translation.Regex = new Regex(sb.ToString(), RegexOptions.Singleline);
        return translation;
    }

    private abstract record ReplToken;

    private sealed record LiteralToken(string Text) : ReplToken;

    private sealed record GroupToken(int Group) : ReplToken;

    /// <summary>引用某个英文捕获组并统一大小写（替换侧的 {大写英文} / {小写英文}）。</summary>
    private sealed record CaseToken(int Group, bool ToUpper) : ReplToken;

    /// <summary>引用后缀名捕获组，输出时补上点（替换侧的 {后缀名}）。</summary>
    private sealed record ExtToken(int Group) : ReplToken;

    /// <summary>
    /// 把替换文本拆成 token。识别中文变量（{英文}→对应捕获组、{大写英文}→英文组转大写、
    /// {英文=ED}→字面量 ED、{后缀名=MKV}→字面量 .MKV）；旧的 # / {*} 位置引用仍按出现顺序对应捕获组。
    /// </summary>
    private static List<ReplToken> ParseReplacementTokens(string replaceText, PatternTranslation t, out bool hasNamed)
    {
        var tokens = new List<ReplToken>();
        hasNamed = false;
        var positional = 0;

        for (var i = 0; i < replaceText.Length;)
        {
            var ch = replaceText[i];

            if (ch == '{' && TryParseVariable(replaceText, i, out var name, out var index, out var value, out var len))
            {
                i += len;
                if (name == "后缀名" && value is not null)
                {
                    hasNamed = true;
                    tokens.Add(new LiteralToken("." + value));
                    continue;
                }

                if (value is not null)
                {
                    // {英文=ED} / {数字=01} …：替换侧带值 = 直接输出该字面量
                    hasNamed = true;
                    tokens.Add(new LiteralToken(value));
                    continue;
                }

                hasNamed = true;
                switch (name)
                {
                    case "英文":
                    {
                        var g = ResolveGroup(t.EnglishGroups, index);
                        if (g > 0) tokens.Add(new GroupToken(g));
                        break;
                    }
                    case "大写英文":
                    {
                        var g = ResolveGroup(t.EnglishGroups, index);
                        if (g > 0) tokens.Add(new CaseToken(g, ToUpper: true));
                        break;
                    }
                    case "小写英文":
                    {
                        var g = ResolveGroup(t.EnglishGroups, index);
                        if (g > 0) tokens.Add(new CaseToken(g, ToUpper: false));
                        break;
                    }
                    case "数字":
                    {
                        var g = ResolveGroup(t.DigitGroups, index);
                        if (g > 0) tokens.Add(new GroupToken(g));
                        break;
                    }
                    case "中文":
                    {
                        var g = ResolveGroup(t.ChineseGroups, index);
                        if (g > 0) tokens.Add(new GroupToken(g));
                        break;
                    }
                    case "后缀名":
                    {
                        var g = ResolveGroup(t.ExtGroups, index);
                        if (g > 0) tokens.Add(new ExtToken(g));
                        break;
                    }
                }

                continue;
            }

            if (ch == '#')
            {
                while (i < replaceText.Length && replaceText[i] == '#') i++;
                positional++;
                tokens.Add(new GroupToken(positional));
                continue;
            }

            if (i + 2 < replaceText.Length && ch == '{' && replaceText[i + 1] == '*' && replaceText[i + 2] == '}')
            {
                positional++;
                tokens.Add(new GroupToken(positional));
                i += 3;
                continue;
            }

            tokens.Add(new LiteralToken(ch.ToString()));
            i++;
        }

        return tokens;
    }

    /// <summary>按序号取第 N 个捕获组编号；缺省取第 1 个，越界收敛到最后一个，没有返回 -1。</summary>
    private static int ResolveGroup(List<int> groups, int? index)
    {
        if (groups.Count == 0) return -1;
        var i = (index ?? 1) - 1;
        if (i < 0) i = 0;
        if (i >= groups.Count) i = groups.Count - 1;
        return groups[i];
    }

    private static string RenderReplacement(List<ReplToken> tokens, Match m)
    {
        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            switch (token)
            {
                case LiteralToken lit:
                    sb.Append(lit.Text);
                    break;
                case GroupToken grp:
                    if (grp.Group > 0 && grp.Group < m.Groups.Count) sb.Append(m.Groups[grp.Group].Value);
                    break;
                case CaseToken cs:
                    if (cs.Group > 0 && cs.Group < m.Groups.Count)
                    {
                        var v = m.Groups[cs.Group].Value;
                        sb.Append(cs.ToUpper ? v.ToUpperInvariant() : v.ToLowerInvariant());
                    }

                    break;
                case ExtToken ext:
                    if (ext.Group > 0 && ext.Group < m.Groups.Count) sb.Append('.').Append(m.Groups[ext.Group].Value);
                    break;
            }
        }

        return sb.ToString();
    }
}
