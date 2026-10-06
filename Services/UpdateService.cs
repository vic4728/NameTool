using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NameTool.Services;

public sealed class UpdateService
{
    private const string BaseUrl = "https://svr.jcsit.cn/NameTool/update/";
    private const string ListApiUrl = BaseUrl + "?json";

    private static readonly HttpClient CheckHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// API: GET /update/?json → { "code": 200, "data": { "files": [{ "name", "size", "size_human", "modified", "url" }] } }
    /// 从文件名解析版本号，与当前版本比对。
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            var json = await CheckHttp.GetStringAsync(ListApiUrl);
            var apiResp = JsonSerializer.Deserialize<ApiResponse<UpdateListData>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (apiResp?.Data is null || apiResp.Data.Files.Count == 0)
            {
                return new UpdateCheckResult { Status = UpdateStatus.Failed, Message = "服务器无可用更新文件" };
            }

            // 从文件列表中找到版本号最高的 Setup 文件
            var currentVersion = GetCurrentVersion();
            UpdateFileEntry? latestFile = null;
            var latestVersion = currentVersion;

            foreach (var file in apiResp.Data.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Name)) continue;
                // 匹配 NameTool_v1.2.0_Setup.exe 或 NameTool_1.2.0_setup.exe 等格式
                var ver = ParseVersionFromFileName(file.Name);
                if (ver is null) continue;
                if (ver > latestVersion)
                {
                    latestVersion = ver;
                    latestFile = file;
                }
            }

            if (latestFile is null)
            {
                return new UpdateCheckResult { Status = UpdateStatus.UpToDate, Message = $"当前版本 v{currentVersion} 已是最新版本" };
            }

            var downloadUrl = string.IsNullOrWhiteSpace(latestFile.Url)
                ? $"{BaseUrl}?file={Uri.EscapeDataString(latestFile.Name!)}"
                : latestFile.Url;

            // 取该版本的大白话更新说明；服务器没写就留给 UI 用兜底文案
            List<string>? notes = null;
            if (apiResp.Data.ReleaseNotes is not null)
            {
                apiResp.Data.ReleaseNotes.TryGetValue(latestVersion.ToString(), out notes);
            }

            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpdateAvailable,
                Message = $"发现新版本 v{latestVersion}（当前 v{currentVersion}）",
                NewVersion = latestVersion.ToString(),
                FileName = latestFile.Name,
                FileSize = latestFile.SizeHuman,
                DownloadUrl = downloadUrl,
                Notes = notes
            };
        }
        catch (HttpRequestException)
        {
            return new UpdateCheckResult { Status = UpdateStatus.Failed, Message = "无法连接更新服务器" };
        }
        catch (TaskCanceledException)
        {
            return new UpdateCheckResult { Status = UpdateStatus.Failed, Message = "连接更新服务器超时" };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult { Status = UpdateStatus.Failed, Message = $"检查更新失败：{ex.Message}" };
        }
    }

    /// <summary>
    /// API: GET /update/?file=NameTool_v1.2.0_Setup.exe → 二进制流
    /// 下载到 %TEMP%\NameToolUpdate\ 并返回本地路径。
    /// </summary>
    public async Task<string?> DownloadUpdateAsync(string fileName, string? downloadUrlFromApi, IProgress<int> progress, CancellationToken cancellationToken)
    {
        var downloadUrl = string.IsNullOrWhiteSpace(downloadUrlFromApi)
            ? $"{BaseUrl}?file={Uri.EscapeDataString(fileName)}"
            : downloadUrlFromApi;
        var tempDir = Path.Combine(Path.GetTempPath(), "NameToolUpdate");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, fileName);

        try
        {
            using var downloadClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var bytes = await downloadClient.GetByteArrayAsync(downloadUrl, linkedCts.Token);

            File.WriteAllBytes(tempPath, bytes);
            progress.Report(100);
            return tempPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("用户取消了下载");
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("下载超时（10分钟）");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"下载失败：{ex.Message}", ex);
        }
        finally
        {
            try { if (File.Exists(tempPath)) { } } catch { /* ok */ }
        }
    }

    public static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return Version.TryParse(infoVersion, out var v) ? v : new Version(0, 0, 0);
    }

    /// <summary>「忽略更新」的落盘文件名（内容就是一个版本号字符串，如 2.0.0）。</summary>
    private const string IgnoredVersionFileName = "update-ignored.txt";

    /// <summary>
    /// 用户「忽略更新」时记下的版本号；没记过返回 null。
    /// 只影响启动自动检查的提示（该版本不再亮红字）；手动点「检查更新」永远照常弹窗。
    /// </summary>
    public static string? GetIgnoredVersion()
    {
        foreach (var dir in AppDataPaths.ReadDirectories)
        {
            try
            {
                var path = Path.Combine(dir, IgnoredVersionFileName);
                if (!File.Exists(path)) continue;

                var text = File.ReadAllText(path).Trim();
                if (text.Length > 0) return text;
            }
            catch
            {
                // 单个候选目录读不了（不存在 / 无权限）就找下一个
            }
        }

        return null;
    }

    /// <summary>记录 / 清除忽略的版本号（<paramref name="version"/> 为空即清除）。写失败静默——只是使用习惯，不值得拦人。</summary>
    public static void SetIgnoredVersion(string? version)
    {
        try
        {
            var path = AppDataPaths.Combine(IgnoredVersionFileName);
            AppDataPaths.TryEnsureDirectory(AppDataPaths.PrimaryDirectory);

            if (string.IsNullOrWhiteSpace(version))
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                File.WriteAllText(path, version.Trim());
            }
        }
        catch
        {
            // 写不进去不影响本次使用，只是下次启动会再提示一次
        }
    }

    /// <summary>
    /// 从文件名中提取版本号，如 NameTool_v1.2.0_Setup.exe → 1.2.0
    /// </summary>
    private static Version? ParseVersionFromFileName(string fileName)
    {
        // 匹配 _v1.2.3 或 _1.2.3 格式的版本号
        var match = Regex.Match(fileName, @"_v?(\d+\.\d+\.\d+)");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var v) ? v : null;
    }

    // API 响应模型
    private sealed class ApiResponse<T>
    {
        public int Code { get; set; }
        public string? Message { get; set; }
        public T? Data { get; set; }
    }

    private sealed class UpdateListData
    {
        public int Total { get; set; }
        public List<UpdateFileEntry> Files { get; set; } = [];

        /// <summary>版本号 → 大白话更新内容列表（来自服务器 release-notes.json）。</summary>
        public Dictionary<string, List<string>>? ReleaseNotes { get; set; }
    }

    private sealed class UpdateFileEntry
    {
        public string? Name { get; set; }
        public long Size { get; set; }
        public string? SizeHuman { get; set; }
        public string? Modified { get; set; }
        public string? Url { get; set; }
    }
}

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    Failed
}

public sealed class UpdateCheckResult
{
    public required UpdateStatus Status { get; init; }
    public required string Message { get; init; }
    public string? NewVersion { get; init; }
    public string? FileName { get; init; }
    public string? FileSize { get; init; }
    public string? DownloadUrl { get; init; }

    /// <summary>本次更新的内容列表（大白话）；服务器未提供时为 null。</summary>
    public List<string>? Notes { get; init; }
}
