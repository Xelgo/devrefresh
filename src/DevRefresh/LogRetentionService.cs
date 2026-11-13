using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DevRefresh;

internal sealed partial class LogRetentionService(string logPath, Store store, ILogger<LogRetentionService> logger)
    : BackgroundService
{
    [GeneratedRegex(@"\Adevrefresh-[0-9]{8}(?:_[0-9]+)?\.jsonl\z", RegexOptions.CultureInvariant)]
    private static partial Regex LogFileName();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var days = store.GetLogRetentionDays();
                var deleted = DeleteExpiredLogs(days, stoppingToken);
                if (deleted > 0)
                    logger.LogInformation("Удалено файлов журнала старше {Days} дней: {Count}", days, deleted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                logger.LogWarning(ex, "Не удалось очистить каталог журналов DevRefresh");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private int DeleteExpiredLogs(int days, CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(logPath, "devrefresh-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            stoppingToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            if (!LogFileName().IsMatch(file.Name)) continue;
            try
            {
                if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0
                    || (now - file.LastWriteTimeUtc).TotalDays <= days) continue;

                // Deny writers while confirming age and deleting the closed log.
                using var guard = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                file.Refresh();
                if ((now - file.LastWriteTimeUtc).TotalDays <= days) continue;
                file.Delete();
                deleted++;
            }
            catch (FileNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Не удалось удалить файл журнала {FileName}", file.Name);
            }
        }
        return deleted;
    }
}
