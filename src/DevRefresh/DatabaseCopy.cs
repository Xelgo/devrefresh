using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using static DevRefresh.RestoreSql;

namespace DevRefresh;

internal static class DatabaseCopy
{
    public static string PreviousName(string id) => "DevRefreshPrevious_" + id;
    public static string BackupName(string id) => "devrefresh-copy-" + id + ".bak";

    public static (string AppDirectory,string SqlDirectory)? BackupDirectories(SqlSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CopyBackupDirectory) && !string.IsNullOrWhiteSpace(settings.SqlCopyBackupDirectory))
            return (settings.CopyBackupDirectory,settings.SqlCopyBackupDirectory);
        if (ShrinkBackups.Configured(settings))
            return (settings.ShrinkBackupDirectory,settings.SqlShrinkBackupDirectory);
        return null;
    }

    public static void DeleteBackup(WorkflowJob job)
    {
        if (!Guid.TryParseExact(job.Id,"N",out _) || job.ResourceName!=BackupName(job.Id) ||
            BackupDirectories(job.Request.Settings) is not { } paths)
            throw new InputException("Временный .bak не зарегистрирован для этого задания.");
        File.Delete(Path.Combine(paths.AppDirectory,job.ResourceName));
    }

    public static async Task CreateBackup(SqlConnection connection, WorkflowJob job, string source, string sqlPath,
        Action<string> progress, ILogger logger, CancellationToken token)
    {
        var command = $"BACKUP DATABASE {Q(source)} TO DISK={L(sqlPath)} WITH COPY_ONLY,COMPRESSION,CHECKSUM,INIT";
        try
        {
            await Execute(connection, command, token);
        }
        catch (SqlException error) when (IsNetworkWriteFailure(error, sqlPath))
        {
            logger.LogWarning(error, "Temporary backup write failed on a network share for job {JobId}", job.Id);
            progress("Повтор создания временного .bak после сетевого сбоя");
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            try
            {
                DeleteBackup(job);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(cleanupError, "Could not remove incomplete temporary backup before retry for job {JobId}", job.Id);
                throw NetworkFailure(error, "Не удалось удалить неполный файл для повтора.");
            }

            try
            {
                await Execute(connection, command, token);
            }
            catch (SqlException retryError) when (IsNetworkWriteFailure(retryError, sqlPath))
            {
                throw NetworkFailure(retryError, "Сбой повторился при записи.");
            }
        }
    }

    private static bool IsNetworkWriteFailure(SqlException error, string sqlPath) =>
        sqlPath.StartsWith(@"\\", StringComparison.Ordinal) &&
        error.Errors.Cast<SqlError>().Any(item =>
            item.Number is 3202 or 3271 or 3634 &&
            Regex.IsMatch(item.Message, @"[':\s](?:59|64)\s*\(", RegexOptions.CultureInvariant));

    private static SqlServiceException NetworkFailure(SqlException error, string detail) =>
        new($"SQL Server потерял доступ к сетевой папке при создании временного .bak. {detail} " +
            "Укажите локальный путь на сервере SQL в настройке временных .bak или проверьте доступность сетевой папки. " +
            error.Message, error.Number, error);

    public static async Task<bool> TryRefreshLock(SqlConnection connection,string database,string mode,CancellationToken token)
    {
        var resource = L("DevRefresh.Publish."+database.ToLowerInvariant());
        return await Scalar<int>(connection,$"""
            DECLARE @r int;
            EXEC @r=sys.sp_getapplock @Resource={resource},@LockMode={L(mode)},@LockOwner='Session',@LockTimeout=0;
            SELECT @r;
            """,token)>=0;
    }

    public static Task FinishPublish(SqlConnection connection,string target,string stage,string previous,CancellationToken token) =>
        Execute(connection,$"""
            IF DB_ID({L(previous)}) IS NOT NULL
            BEGIN
                IF DB_ID({L(target)}) IS NULL
                    ALTER DATABASE {Q(previous)} MODIFY NAME={Q(target)};
                ELSE IF DB_ID({L(stage)}) IS NOT NULL
                    THROW 50001, N'Не удалось однозначно определить результат замены базы назначения.', 1;
                ELSE
                    DROP DATABASE {Q(previous)};
            END;
            IF EXISTS(SELECT 1 FROM sys.databases WHERE name={L(target)} AND user_access=1)
                ALTER DATABASE {Q(target)} SET MULTI_USER WITH NO_WAIT;
            """,token);
}
