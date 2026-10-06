using System;
using System.IO;
using System.Text;

namespace NameTool.Services;

/// <summary>
/// 落盘日志（用户要求，2026-10-05）：在数据目录（exe 同级 <c>data\</c>，装进
/// <c>Program Files</c> 等只读位置自动落到 <c>%LOCALAPPDATA%\NameTool</c>）生成
/// <c>logs\NameTool.log</c>，每行带时间戳。
/// <para>
/// **上限 5MB**：写入前检查文件大小，达到上限就整份删除、从头重新记录
/// （新文件首行注明清空时间），不搞多份滚动文件 —— 用户明确说「清除旧日志文件重新记录」。
/// 全程 try/catch 兜底：日志落盘绝不能反过来影响改名等功能；跨线程安全（改名回调可能来自后台线程）。
/// </para>
/// </summary>
public static class FileLogSink
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>
    /// DEBUG 级开关（用户要求，2026-10-06）：true 时 <see cref="Debug"/> 记录的信息（各操作
    /// 返回码 / 接口原文等）也进窗口与文件。默认 false（只记 INFO / ERROR）。
    /// 由「设置」持久化（UiStateStore），见 MainViewModel.DebugLogEnabled。
    /// </summary>
    public static bool DebugEnabled { get; set; }

    /// <summary>DEBUG 级记录：仅在 <see cref="DebugEnabled"/> 开启时进窗口与文件（级别 [DEBUG]）。</summary>
    public static void Debug(string message)
    {
        if (!DebugEnabled) return;
        Write(message, "DEBUG");
    }

    /// <summary>程序启动时调用一次，指定日志文件路径（传 null 或空串 = 不落盘）。</summary>
    public static void Configure(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>
    /// 追加一行日志；超过 5MB 上限先清空文件再从头记录。
    /// <para>
    /// 日志级别（用户要求，2026-10-05）：文件里每行带 <c>[INFO]</c> / <c>[ERROR]</c> 前缀 ——
    /// <paramref name="level"/> 显式指定优先；不指定时按内容自动判定（含 <c>[FAIL]</c> 即 ERROR），
    /// 这样 115 侧经 <c>Action&lt;string&gt;</c> 委托进来的失败行也能正确标级。
    /// </para>
    /// </summary>
    public static void Write(string message, string? level = null)
    {
        if (_path is null) return;

        var resolvedLevel = level ?? (message.Contains("[FAIL]", StringComparison.Ordinal) ? "ERROR" : "INFO");

        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(_path) && new FileInfo(_path).Length >= MaxBytes)
                {
                    File.Delete(_path);
                    var cleared = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] —— 日志已达到 5MB 上限，旧日志已清除，从此处重新记录 ——";
                    File.AppendAllText(_path, cleared + Environment.NewLine, new UTF8Encoding(false));
                }

                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{resolvedLevel}] {message}";
                File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 磁盘写不进去（只读目录 / 被占用）时静默放弃：日志是辅助，功能优先
        }
    }
}
