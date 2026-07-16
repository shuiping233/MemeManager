using System;
using System.IO;
using System.Threading;

namespace MemeManager;

/// <summary>
/// 统一日志：始终输出到调试通道（Debug.WriteLine），
/// 若配置中“保存日志文件”开启，则同时追加写入数据目录下的 log/debug.log。
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private const long MaxFileBytes = 5 * 1024 * 1024; // 单文件上限 5MB

    public static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);

        try
        {
            var cfg = App.DataEngine?.Config;
            if (cfg is null || !cfg.SaveLogFile)
                return;

            var baseDir = App.DataEngine?.BaseDir;
            if (string.IsNullOrEmpty(baseDir))
                return;

            var logDir = Path.Combine(baseDir, "log");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "debug.log");

            // 超过上限则清空重新开始，避免无限增长
            if (File.Exists(logPath))
            {
                var info = new FileInfo(logPath);
                if (info.Length > MaxFileBytes)
                    File.WriteAllText(logPath, string.Empty);
            }

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(logPath, line);
            }
        }
        catch
        {
            // 日志写入失败不应影响主程序
        }
    }
}
