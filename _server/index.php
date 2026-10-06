<?php
/**
 * 桌面客户端更新目录
 *
 * 提供文件列表与下载服务，供桌面客户端自动更新使用。
 * 支持下载次数统计，数据存储在同目录 downloads.json 中。
 *
 * 用法:
 *   GET /update/              → 返回 HTML 文件列表
 *   GET /update/?json         → 返回 JSON 文件列表（含下载次数与 release_notes）
 *   GET /update/?stats        → 返回 HTML 升级 / 下载统计页（每版本总量）
 *   GET /update/?file=xxx.exe → 下载指定文件并计数
 *
 * 目录自清理：任意请求进来时，自动只保留版本号最新的 KEEP_VERSIONS 个版本
 * （每版本 = Setup.exe 安装包 + Portable.zip 便携包），更旧版本的文件直接删除。
 * downloads.json 的历史计数不删，统计页照常显示「已下架」版本。
 */
$dir = __DIR__;
$countFile = $dir . '/downloads.json';
$notesFile = $dir . '/release-notes.json';

/**
 * 只保留最新 KEEP_VERSIONS 个版本的发布文件（每版本 = 安装包 Setup.exe + 便携包 Portable.zip），
 * 更旧版本的两种文件都删。幂等且并发安全（unlink 失败静默继续）。
 * 2026-10-06 起：便携绿色版与安装版同时发布，裁剪按「版本号」去重而非文件数。
 */
define('KEEP_VERSIONS', 2);

function pruneOldInstallers($dir) {
    // 文件名 => 版本号（两种命名都认）
    $files = [];
    foreach (scandir($dir) ?: [] as $entry) {
        if (preg_match('/^NameTool_v(\d+(?:\.\d+)+)_Setup\.exe$/i', $entry, $m)) {
            $files[$entry] = $m[1];
        } elseif (preg_match('/^NameTool_v(\d+(?:\.\d+)+)_Portable\.zip$/i', $entry, $m)) {
            $files[$entry] = $m[1];
        }
    }
    if (count($files) <= KEEP_VERSIONS * 2) {
        return;
    }
    $versions = array_unique(array_values($files));
    usort($versions, function ($a, $b) {
        return version_compare($b, $a); // 版本号新的在前
    });
    $keepVersions = array_slice($versions, 0, KEEP_VERSIONS);
    foreach ($files as $name => $ver) {
        if (!in_array($ver, $keepVersions, true)) {
            @unlink($dir . '/' . $name);
        }
    }
}
pruneOldInstallers($dir);

/**
 * 读取下载计数
 */
function loadDownloadCounts($file) {
    if (!file_exists($file)) {
        return [];
    }
    $content = file_get_contents($file);
    if ($content === false) {
        return [];
    }
    $data = json_decode($content, true);
    return is_array($data) ? $data : [];
}

/**
 * 写入下载计数（使用 flock 防并发）
 */
function saveDownloadCounts($file, $counts) {
    $fp = fopen($file, 'c');
    if ($fp === false) {
        return false;
    }
    if (flock($fp, LOCK_EX)) {
        ftruncate($fp, 0);
        rewind($fp);
        fwrite($fp, json_encode($counts, JSON_PRETTY_PRINT));
        flock($fp, LOCK_UN);
    }
    fclose($fp);
    return true;
}

/**
 * 递增下载计数
 */
function incrementDownloadCount($file, $filename) {
    $counts = loadDownloadCounts($file);
    if (!isset($counts[$filename])) {
        $counts[$filename] = 0;
    }
    $counts[$filename]++;
    saveDownloadCounts($file, $counts);
    return $counts[$filename];
}

/**
 * 读取版本更新说明（大白话文案，键为版本号如 "1.9.4"）
 */
function loadReleaseNotes($file) {
    if (!file_exists($file)) {
        return [];
    }
    $content = file_get_contents($file);
    if ($content === false) {
        return [];
    }
    $data = json_decode($content, true);
    return is_array($data) ? $data : [];
}

// ====== 下载文件 ======
if (isset($_GET['file'])) {
    $filename = basename($_GET['file']); // 防止路径遍历
    $filepath = $dir . '/' . $filename;

    if (!is_file($filepath)) {
        http_response_code(404);
        header('Content-Type: application/json; charset=utf-8');
        echo json_encode(['code' => 404, 'message' => 'File not found: ' . $filename]);
        exit;
    }

    $filesize = filesize($filepath);

    // 仅对非断点续传的初始请求计数
    if (!isset($_SERVER['HTTP_RANGE'])) {
        incrementDownloadCount($countFile, $filename);
    }

    // 使用 X-Accel-Redirect 让 nginx 直接提供文件下载，完全绕过 PHP 内存限制
    // nginx 通过 internal location /internal-update-files/ 映射到实际磁盘路径
    header('Content-Type: application/octet-stream');
    header('Content-Disposition: attachment; filename="' . $filename . '"');
    header('Accept-Ranges: bytes');

    if (isset($_SERVER['HTTP_RANGE'])) {
        $range = $_SERVER['HTTP_RANGE'];
        $range = str_replace('bytes=', '', $range);
        list($start, $end) = explode('-', $range);

        $start = intval($start);
        $end = empty($end) ? $filesize - 1 : intval($end);

        if ($start > $end || $start >= $filesize || $end >= $filesize) {
            http_response_code(416);
            header('Content-Range: bytes 0-' . ($filesize - 1) . '/' . $filesize);
            exit;
        }

        http_response_code(206);
        header('Content-Range: bytes ' . $start . '-' . $end . '/' . $filesize);
        header('Content-Length: ' . ($end - $start + 1));
        header('X-Accel-Redirect: /internal-update-files/' . $filename);
        header('X-Accel-Buffering: no');
    } else {
        header('Content-Length: ' . $filesize);
        header('X-Accel-Redirect: /internal-update-files/' . $filename);
        header('X-Accel-Buffering: no');
    }
    exit;
}

// ====== 文件列表 ======
$counts = loadDownloadCounts($countFile);
$notes = loadReleaseNotes($notesFile);
$files = [];
$entries = scandir($dir);

foreach ($entries as $entry) {
    if ($entry === '.' || $entry === '..' || $entry === 'index.php' || $entry === '.htaccess' || $entry === 'downloads.json' || $entry === 'release-notes.json' || str_ends_with($entry, '.bak') || str_contains($entry, '.bak.')) {
        continue;
    }

    $fullPath = $dir . '/' . $entry;
    if (!is_file($fullPath)) {
        continue;
    }

    $files[] = [
        'name' => $entry,
        'size' => filesize($fullPath),
        'size_human' => human_readable_size(filesize($fullPath)),
        'modified' => date('Y-m-d H:i:s', filemtime($fullPath)),
        'downloads' => intval($counts[$entry] ?? 0),
        'url' => $_SERVER['REQUEST_SCHEME'] . '://' . $_SERVER['HTTP_HOST'] . strtok($_SERVER['REQUEST_URI'], '?') . '?file=' . urlencode($entry),
    ];
}

usort($files, function ($a, $b) {
    return strcmp($b['modified'], $a['modified']);
});

$accept = $_SERVER['HTTP_ACCEPT'] ?? '';
if (!isset($_GET['stats']) && (strpos($accept, 'application/json') !== false || isset($_GET['json']))) {
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode([
        'code' => 200,
        'message' => 'ok',
        'data' => [
            'total' => count($files),
            'files' => $files,
            'release_notes' => $notes,
        ],
    ], JSON_UNESCAPED_UNICODE | JSON_PRETTY_PRINT);
    exit;
}

// ====== 升级 / 下载统计页 ======
if (isset($_GET['stats'])) {
    // 按版本聚合：以 downloads.json 全部键（含文件已下架的历史版本）为基础，
    // 再补上目录里有文件但尚无计数的版本。
    $rows = []; // version => ['count', 'mtime', 'exists']

    foreach ($counts as $name => $count) {
        if (preg_match('/_v?(\d+(?:\.\d+)+)_Setup\.exe$/i', $name, $m)) {
            $ver = $m[1];
        } else {
            $ver = null; // 不带版本号的计数项，最后单独列出
        }
        $key = $ver ?? ('?' . $name);
        if (!isset($rows[$key])) {
            $rows[$key] = ['label' => $ver !== null ? 'v' . $ver : $name, 'version' => $ver, 'count' => 0, 'mtime' => null, 'exists' => false];
        }
        $rows[$key]['count'] += intval($count);
    }

    foreach ($entries as $entry) {
        if (!preg_match('/_v?(\d+(?:\.\d+)+)_Setup\.exe$/i', $entry, $m)) {
            continue;
        }
        $ver = $m[1];
        if (!isset($rows[$ver])) {
            $rows[$ver] = ['label' => 'v' . $ver, 'version' => $ver, 'count' => 0, 'mtime' => null, 'exists' => false];
        }
        $rows[$ver]['exists'] = true;
        $rows[$ver]['mtime'] = filemtime($dir . '/' . $entry);
    }

    usort($rows, function ($a, $b) {
        if ($a['version'] === null && $b['version'] === null) return 0;
        if ($a['version'] === null) return 1;
        if ($b['version'] === null) return -1;
        return version_compare($b['version'], $a['version']);
    });

    $total = 0;
    foreach ($rows as $r) $total += $r['count'];
    $maxCount = 0;
    foreach ($rows as $r) $maxCount = max($maxCount, $r['count']);
    $latest = $rows[0]['version'] ?? null;
    $latestCount = $rows[0]['count'] ?? 0;
    ?>
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>NameTool 升级 / 下载统计</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; background: #f5f7fa; color: #303133; padding: 20px; }
        .container { max-width: 860px; margin: 0 auto; }
        h1 { font-size: 20px; margin-bottom: 16px; color: #409eff; }
        h1 a { font-size: 13px; font-weight: normal; margin-left: 12px; }
        .cards { display: flex; gap: 12px; margin-bottom: 16px; }
        .card { flex: 1; background: #fff; border-radius: 4px; padding: 16px 20px; box-shadow: 0 1px 4px rgba(0,0,0,0.08); }
        .card .num { font-size: 26px; font-weight: 700; color: #303133; font-variant-numeric: tabular-nums; }
        .card .label { font-size: 12px; color: #909399; margin-top: 4px; }
        table { width: 100%; border-collapse: collapse; background: #fff; border-radius: 4px; overflow: hidden; box-shadow: 0 1px 4px rgba(0,0,0,0.08); }
        th, td { padding: 10px 16px; text-align: left; border-bottom: 1px solid #ebeef5; }
        th { background: #fafafa; font-weight: 600; font-size: 13px; color: #909399; }
        td { font-size: 14px; font-variant-numeric: tabular-nums; }
        td.num, th.num { text-align: right; }
        .bar-wrap { background: #f0f2f5; border-radius: 3px; height: 14px; width: 100%; overflow: hidden; }
        .bar { height: 100%; background: linear-gradient(90deg, #79bbff, #409eff); border-radius: 3px; }
        .bar.max { background: linear-gradient(90deg, #95d475, #67c23a); }
        .pct { color: #909399; font-size: 12px; margin-left: 8px; }
        .gone { color: #c0c4cc; font-size: 12px; margin-left: 6px; }
        .foot { margin-top: 12px; font-size: 12px; color: #909399; }
    </style>
</head>
<body>
    <div class="container">
        <h1>NameTool 升级 / 下载统计<a href="./">← 返回文件列表</a></h1>
        <div class="cards">
            <div class="card"><div class="num"><?= number_format($total) ?></div><div class="label">总下载 / 升级次数</div></div>
            <div class="card"><div class="num"><?= count($rows) ?></div><div class="label">累计发布版本数</div></div>
            <div class="card"><div class="num"><?= htmlspecialchars($latest !== null ? $latest : '—') ?></div><div class="label">最新版本（<?= number_format($latestCount) ?> 次）</div></div>
        </div>
        <?php if (empty($rows)): ?>
            <table><tbody><tr><td>暂无统计记录</td></tr></tbody></table>
        <?php else: ?>
            <table>
                <thead>
                    <tr>
                        <th>版本</th>
                        <th>发布时间</th>
                        <th class="num">下载次数</th>
                        <th style="width:40%">占比</th>
                    </tr>
                </thead>
                <tbody>
                    <?php foreach ($rows as $r): ?>
                    <tr>
                        <td><?= htmlspecialchars($r['label']) ?><?= $r['exists'] ? '' : '<span class="gone">已下架</span>' ?></td>
                        <td><?= $r['mtime'] !== null ? date('Y-m-d', $r['mtime']) : '—' ?></td>
                        <td class="num"><?= number_format($r['count']) ?></td>
                        <td>
                            <div class="bar-wrap">
                                <?php $pct = $total > 0 ? $r['count'] / $total * 100 : 0; ?>
                                <div class="bar<?= $r['count'] === $maxCount && $maxCount > 0 ? ' max' : '' ?>" style="width:<?= round($pct, 1) ?>%"></div>
                            </div>
                            <span class="pct"><?= round($pct, 1) ?>%</span>
                        </td>
                    </tr>
                    <?php endforeach; ?>
                </tbody>
            </table>
            <p class="foot">统计口径：安装包被完整下载一次计 1 次（客户端升级下载与手动下载均计入；断点续传不重复计数）。数据来自 downloads.json，实时更新。</p>
        <?php endif; ?>
    </div>
</body>
</html>
    <?php
    exit;
}
?>
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>NameTool 更新文件</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; background: #f5f7fa; color: #303133; padding: 20px; }
        .container { max-width: 860px; margin: 0 auto; }
        h1 { font-size: 20px; margin-bottom: 16px; color: #409eff; }
        h1 a { font-size: 13px; font-weight: normal; margin-left: 12px; }
        table { width: 100%; border-collapse: collapse; background: #fff; border-radius: 4px; overflow: hidden; box-shadow: 0 1px 4px rgba(0,0,0,0.08); }
        th, td { padding: 12px 16px; text-align: left; border-bottom: 1px solid #ebeef5; }
        th { background: #fafafa; font-weight: 600; font-size: 13px; color: #909399; }
        td { font-size: 14px; }
        td.downloads { text-align: center; font-variant-numeric: tabular-nums; }
        a { color: #409eff; text-decoration: none; }
        a:hover { text-decoration: underline; }
        .empty { text-align: center; padding: 40px; color: #909399; }
    </style>
</head>
<body>
    <div class="container">
        <h1>更新文件<a href="?stats">升级 / 下载统计 →</a></h1>
        <?php if (empty($files)): ?>
            <p class="empty">暂无更新文件</p>
        <?php else: ?>
            <table>
                <thead>
                    <tr>
                        <th>文件名</th>
                        <th>大小</th>
                        <th>修改时间</th>
                        <th>下载次数</th>
                        <th>操作</th>
                    </tr>
                </thead>
                <tbody>
                    <?php foreach ($files as $f): ?>
                    <tr>
                        <td><a href="?file=<?= urlencode($f['name']) ?>"><?= htmlspecialchars($f['name']) ?></a></td>
                        <td><?= $f['size_human'] ?></td>
                        <td><?= $f['modified'] ?></td>
                        <td class="downloads"><?= number_format($f['downloads']) ?></td>
                        <td><a href="?file=<?= urlencode($f['name']) ?>">下载</a></td>
                    </tr>
                    <?php endforeach; ?>
                </tbody>
            </table>
        <?php endif; ?>
    </div>
</body>
</html>
<?php

function human_readable_size($bytes) {
    if ($bytes >= 1073741824) {
        return round($bytes / 1073741824, 2) . ' GB';
    }
    if ($bytes >= 1048576) {
        return round($bytes / 1048576, 2) . ' MB';
    }
    if ($bytes >= 1024) {
        return round($bytes / 1024, 2) . ' KB';
    }
    return $bytes . ' B';
}
