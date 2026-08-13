using System.Text;
using MemeManager.Services;

namespace MemeManager.Infrastructure;

/// <summary>
/// 统一日志：始终输出到调试通道（Debug.WriteLine），
/// 若配置中"保存日志文件"开启，则同时追加写入数据目录下的 log/debug.log。
/// 写日志复用常驻的 StreamWriter（减少频繁开关文件），若文件被外部删除或写入异常则自动重建。
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private const long MaxFileBytes = 5 * 1024 * 1024; // 单文件上限 5MB
    private const int ReopenLimitMs = 1000;            // 重建失败后最短重试间隔，避免异常风暴

    private static ConfigService ConfigService => App.GetService<ConfigService>();
    private static bool SaveLogEnabled => ConfigService.Config?.SaveLogFile ?? false;

    // 常驻写入器；延迟到首次写入时按当前 BaseDir 打开。
    private static StreamWriter? _writer;
    private static string? _openedPath;
    private static long _lastReopenError = 0;

    public static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);

        try
        {
            if (!SaveLogEnabled)
                return;

            var baseDir = App.DataEngine?.BaseDir;
            if (string.IsNullOrEmpty(baseDir))
                return;

            var logDir = Path.Combine(baseDir, "log");
            var logPath = Path.Combine(logDir, "debug.log");

            EnsureWriter(logDir, logPath);
            if (_writer == null)
                return;

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                // 超过上限则重置文件（先关流、清空、重开），保持常驻复用语义。
                try
                {
                    if (File.Exists(logPath))
                    {
                        var info = new FileInfo(logPath);
                        if (info.Length > MaxFileBytes)
                        {
                            _writer.Dispose();
                            _writer = null;
                            File.WriteAllText(logPath, string.Empty);
                            EnsureWriter(logDir, logPath);
                        }
                    }

                    _writer?.Write(line);
                    _writer?.Flush();
                }
                catch
                {
                    // 写入失败（如文件被删/磁盘异常）：丢弃当前写入器，下次重建。
                    TryDisposeWriter();
                }
            }
        }
        catch
        {
            // 日志写入失败不应影响主程序
        }
    }

    // 确保写入器已打开且指向正确路径；路径变更或写入器为空时（重建/首次）重新打开。
    // 重建失败有最短间隔保护，避免异常风暴。
    private static void EnsureWriter(string logDir, string logPath)
    {
        lock (_lock)
        {
            if (_writer != null && _openedPath != null
                && _openedPath.Equals(logPath, StringComparison.OrdinalIgnoreCase))
                return;

            TryDisposeWriter();
            _openedPath = null;

            // 重建冷却：上次失败不久则跳过，避免高频异常刷屏。
            long now = Environment.TickCount64;
            if (now - _lastReopenError < ReopenLimitMs)
                return;

            try
            {
                Directory.CreateDirectory(logDir);
                var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(fs, Encoding.UTF8);
                _openedPath = logPath;
            }
            catch
            {
                _lastReopenError = Environment.TickCount64;
                TryDisposeWriter();
            }
        }
    }

    private static void TryDisposeWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }
}
