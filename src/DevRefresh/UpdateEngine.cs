using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text.Json;

namespace DevRefresh;

public sealed record PackageManifest(string Product, string Version, int SchemaVersion, Dictionary<string, string>? Files);
public sealed record UpdateReceipt(string TargetRoot, string DataDirectory, string PreviousVersion, string NewVersion,
    string State, Dictionary<string, bool> RootFiles);
public sealed record UpdateResult(string Version, string BackupDirectory, string State);

public static class UpdateEngine
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly string[] RootFiles = ["START.cmd", "STARTSERVICE.cmd", "STOPSERVICE.cmd", "CHECK.cmd", "COLLECT-LOGS.cmd", "SETUP-HTTPS.cmd", "UPDATE.cmd", "UPDATE-ONLINE.cmd", "ROLLBACK.cmd", "README.md", "BUILD-INFO.json"];

    public static UpdateResult Apply(string packageRoot, string targetRoot, string dataDirectory)
    {
        packageRoot = Path.GetFullPath(packageRoot);
        targetRoot = ValidateTarget(targetRoot, packageRoot);
        var sourceApp = Inside(packageRoot, "app");
        var targetApp = Inside(targetRoot, "app");
        if (Path.GetFullPath(dataDirectory).StartsWith(targetApp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || SamePath(dataDirectory, targetApp))
            throw new InvalidOperationException("Служебные данные должны находиться вне app. Сначала перенесите их в постоянную папку и задайте DataDirectory.");
        EnsureStopped(targetApp);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(Inside(packageRoot, "BUILD-INFO.json")), Json)
            ?? throw new InvalidOperationException("Нет манифеста пакета.");
        if (manifest.Product != "DevRefresh" || manifest.SchemaVersion != DatabaseMigrator.LatestVersion || manifest.Files is null)
            throw new InvalidOperationException("Пакет не содержит совместимого манифеста обновления.");
        foreach (var (relative, expected) in manifest.Files)
        {
            var file = Inside(packageRoot, relative);
            if (!AllowedPayload(relative) || !File.Exists(file)) throw new InvalidOperationException("Недопустимый файл в пакете обновления.");
            using var stream = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Нарушена контрольная сумма файла {relative}.");
        }
        var previousVersion = FileVersionInfo.GetVersionInfo(Inside(targetRoot, "app/DevRefresh.exe")).FileVersion ?? "0.0.0";
        if (NormalizeVersion(previousVersion) >= NormalizeVersion(manifest.Version))
            throw new InvalidOperationException($"Установлена версия {previousVersion}; пакет {manifest.Version} не является более новым.");
        var database = Path.Combine(dataDirectory, "devrefresh.sqlite");
        if (!File.Exists(database)) throw new InvalidOperationException("Служебная SQLite не найдена. Проверьте путь установки и DataDirectory. Новая пустая БД не будет создана.");
        using var runtimeLock = RuntimeDataLock.Acquire(dataDirectory);
        var backup = Inside(targetRoot, $"backups/update-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var stage = Inside(targetRoot, $".update-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backup);
        if(OperatingSystem.IsWindows()&&File.Exists(Path.Combine(dataDirectory,ServiceKeyPreparation.Marker)))
        {
            // A backup contains machine-protected master keys: retain the private data ACL.
            var access=new DirectoryInfo(dataDirectory).GetAccessControl(AccessControlSections.Access);
            access.SetAccessRuleProtection(true,true);
            new DirectoryInfo(backup).SetAccessControl(access);
        }
        var rootPayload = manifest.Files.Keys.Where(x => !x.StartsWith("app/", StringComparison.Ordinal)).Append("BUILD-INFO.json").Distinct().ToArray();
        var oldFiles = rootPayload.Append("config/server.json").ToDictionary(x => x, x => File.Exists(Inside(targetRoot, x)));
        var receipt = new UpdateReceipt(targetRoot, dataDirectory, previousVersion, manifest.Version, "preparing", oldFiles);
        DatabaseMigrator.Backup(database, Inside(backup, "database.sqlite"));
        if (Directory.Exists(Path.Combine(dataDirectory, "keys"))) CopyDirectory(Path.Combine(dataDirectory, "keys"), Inside(backup, "keys"));
        foreach (var (relative, existed) in oldFiles)
            if (existed) CopyFile(Inside(targetRoot, relative), Inside(backup, "files/" + relative));
        WriteReceipt(backup, receipt);
        var databaseMayHaveChanged = false;
        var oldAppMoved = false;
        var newAppInstalled = false;
        try
        {
            CopyDirectory(sourceApp, Inside(stage, "app"));
            databaseMayHaveChanged = true;
            DatabaseMigrator.Ensure(database);
            // Both source and destination are checked absolute children of this installation.
            Directory.Move(Inside(targetRoot, "app"), Inside(backup, "app"));
            oldAppMoved = true;
            Directory.Move(Inside(stage, "app"), Inside(targetRoot, "app"));
            newAppInstalled = true;
            foreach (var relative in rootPayload) CopyFile(Inside(packageRoot, relative), Inside(targetRoot, relative));
            receipt = receipt with { State = "installed-awaiting-start" };
            WriteReceipt(backup, receipt);
            File.WriteAllText(Inside(targetRoot, "last-update.json"), JsonSerializer.Serialize(new { backupDirectory = backup, version = manifest.Version }, Json));
            if (Directory.Exists(stage)) Directory.Delete(stage); // empty directory only
            return new(manifest.Version, backup, receipt.State);
        }
        catch (Exception failure)
        {
            try
            {
                if (newAppInstalled) Directory.Move(Inside(targetRoot, "app"), Inside(backup, "failed-app"));
                if (oldAppMoved) Directory.Move(Inside(backup, "app"), Inside(targetRoot, "app"));
                if (databaseMayHaveChanged) RestoreDatabase(Inside(backup, "database.sqlite"), database);
                RestoreRootFiles(backup, receipt);
                WriteReceipt(backup, receipt with { State = "automatically-rolled-back" });
            }
            catch (Exception rollbackFailure)
            {
                throw new InvalidOperationException($"Обновление не выполнено; автоматический откат не завершён. Резервная копия: {backup}. Ошибка отката: {rollbackFailure.Message}", failure);
            }
            throw new InvalidOperationException($"Обновление отменено, предыдущая версия восстановлена. Резервная копия: {backup}. Причина: {failure.Message}", failure);
        }
    }

    public static UpdateResult Rollback(string packageRoot, string targetRoot, string dataDirectory, string backup)
    {
        targetRoot = ValidateTarget(targetRoot, Path.GetFullPath(packageRoot));
        backup = Path.GetFullPath(backup);
        _ = Inside(Inside(targetRoot, "backups"), Path.GetRelativePath(Inside(targetRoot, "backups"), backup));
        var receipt = JsonSerializer.Deserialize<UpdateReceipt>(File.ReadAllText(Inside(backup, "update.json")), Json)
            ?? throw new InvalidOperationException("Не найдены данные резервной копии.");
        if (!SamePath(receipt.TargetRoot, targetRoot) || !SamePath(receipt.DataDirectory, dataDirectory)
            || receipt.State != "installed-awaiting-start")
            throw new InvalidOperationException("Резервная копия не соответствует этой установке или уже была использована для отката.");
        if (receipt.RootFiles.Keys.Any(x => !AllowedPayload(x) && x != "config/server.json"))
            throw new InvalidOperationException("Недопустимый состав файлов в резервной копии.");
        var installedVersion = FileVersionInfo.GetVersionInfo(Inside(targetRoot, "app/DevRefresh.exe")).FileVersion ?? "0.0.0";
        if (NormalizeVersion(installedVersion) != NormalizeVersion(receipt.NewVersion))
            throw new InvalidOperationException("Версия установки отличается от версии этого обновления. Для отката используйте последнюю подходящую резервную копию.");
        EnsureStopped(Inside(targetRoot, "app"));
        using var runtimeLock = RuntimeDataLock.Acquire(dataDirectory);
        if (!Directory.Exists(Inside(backup, "app")) || !File.Exists(Inside(backup, "database.sqlite")))
            throw new InvalidOperationException("Резервная копия неполная; откат не начат.");
        // Preserve the current files and a consistent DB snapshot before a user-requested rollback.
        DatabaseMigrator.Backup(Path.Combine(dataDirectory, "devrefresh.sqlite"), Inside(backup, "database-before-rollback.sqlite"));
        Directory.Move(Inside(targetRoot, "app"), Inside(backup, "replaced-app"));
        try
        {
            Directory.Move(Inside(backup, "app"), Inside(targetRoot, "app"));
            RestoreDatabase(Inside(backup, "database.sqlite"), Path.Combine(dataDirectory, "devrefresh.sqlite"));
            RestoreRootFiles(backup, receipt);
            if (Directory.Exists(Inside(backup, "keys"))) CopyDirectory(Inside(backup, "keys"), Path.Combine(dataDirectory, "keys"));
            WriteReceipt(backup, receipt with { State = "rolled-back" });
            return new(receipt.PreviousVersion, backup, "rolled-back");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Откат не завершён. Копии обеих версий и БД сохранены в {backup}; не запускайте сервис до проверки. {ex.Message}", ex);
        }
    }

    private static void RestoreRootFiles(string backup, UpdateReceipt receipt)
    {
        foreach (var (relative, existed) in receipt.RootFiles)
        {
            var destination = Inside(receipt.TargetRoot, relative);
            if (existed) CopyFile(Inside(backup, "files/" + relative), destination);
            else if (File.Exists(destination)) File.Delete(destination);
        }
    }

    private static void RestoreDatabase(string snapshot, string database)
    {
        // Connections are closed and the data-directory lock remains held.
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(database + suffix)) File.Delete(database + suffix);
        File.Copy(snapshot, database, overwrite: true);
    }

    private static bool AllowedPayload(string path) => !path.Contains('\\') && !path.Contains("..", StringComparison.Ordinal)
        && (path.StartsWith("app/", StringComparison.Ordinal) || path.StartsWith("scripts/", StringComparison.Ordinal) || RootFiles.Contains(path));
    private static bool SamePath(string a, string b) => Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar)
        .Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    public static string Inside(string root, string relative)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь выходит за пределы выбранной установки.");
        return result;
    }
    private static string ValidateTarget(string root, string packageRoot)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (SamePath(root, packageRoot) || !File.Exists(Inside(root, "app/DevRefresh.exe")) || !File.Exists(Inside(root, "config/server.json")))
            throw new InvalidOperationException("Выберите существующую установку с app/DevRefresh.exe и config/server.json, отличную от папки нового пакета.");
        foreach (var path in new[] { root, Inside(root, "app"), Inside(root, "config"), Inside(root, "scripts"), Inside(root, "backups") })
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Обновление через ссылки/junction не поддерживается.");
        return root;
    }
    private static void EnsureStopped(string appDirectory)
    {
        var executable = Inside(appDirectory, "DevRefresh.exe");
        foreach (var process in Process.GetProcessesByName("DevRefresh"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (process.MainModule?.FileName is { } file && SamePath(file, executable))
                        throw new InvalidOperationException($"DevRefresh ещё работает (PID {process.Id}). Остановите его через Ctrl+C или службу и повторите обновление.");
                }
                catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("Не удалось проверить работающие процессы DevRefresh. Запустите обновление под владельцем сервиса или администратором.", ex); }
            }
        }
    }
    private static Version NormalizeVersion(string value)
    {
        var version = Version.Parse(value);
        return new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }
    private static void CopyFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Ссылки внутри копируемых каталогов не поддерживаются.");
            if (entry is DirectoryInfo) CopyDirectory(entry.FullName, Inside(destination, entry.Name));
            else CopyFile(entry.FullName, Inside(destination, entry.Name));
        }
    }
    private static void WriteReceipt(string backup, UpdateReceipt receipt) =>
        File.WriteAllText(Inside(backup, "update.json"), JsonSerializer.Serialize(receipt, Json));
}
