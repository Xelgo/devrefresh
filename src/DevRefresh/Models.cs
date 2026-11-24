using System.Security.Claims;
using System.Text.RegularExpressions;

namespace DevRefresh;

public sealed record LocalAdmin(string Username, string PasswordHash, string Version);
public sealed record AppUser(string Name, string Kind, bool IsAdmin, string? OwnerKey,
    bool CanRefreshTestContour = false);
public sealed record SqlSettings(string Server, string Authentication, string Username,
    string ProtectedPassword, string BackupDirectory, string SqlBackupDirectory,
    bool TrustServerCertificate, bool RunDbccCheckDb = true,
    string ShrinkBackupDirectory = "", string SqlShrinkBackupDirectory = "",
    string CopyBackupDirectory = "", string SqlCopyBackupDirectory = "", DtRestoreSettings? DtRestore = null, OneCSettings? OneC = null);
public sealed record SettingsInput(string Server, string Authentication, string Username,
    string? Password, string BackupDirectory, string SqlBackupDirectory,
    bool TrustServerCertificate, bool? RunDbccCheckDb = null,
    string? ShrinkBackupDirectory = null, string? SqlShrinkBackupDirectory = null,
    string? CopyBackupDirectory = null, string? SqlCopyBackupDirectory = null, DtRestoreInput? DtRestore = null, OneCSettings? OneC = null);
public sealed record SetupInput(string Username, string Password, string[]? WindowsAdmins);
public sealed record LoginInput(string Username, string Password);
public sealed record SecurityInput(string[]? WindowsAdmins, string? NewPassword,
    bool? WindowsEnabled = null, bool? WindowsAutoSignIn = null, string[]? TestContourOperators = null);
public sealed record WindowsAccess(bool Enabled, bool AutoSignIn, string[]? TestContourOperators = null);
public sealed record LogRetentionInput(int Days);
public sealed record DatabaseName(string SourceKey, string OwnerKey, bool IsTestContour = false);
public sealed record BackupFile(string Name, long Size, DateTimeOffset ModifiedAt, string SourceKey, DateOnly? BackupDate);
public sealed record DatabaseEntry(string Name, string SourceKey, string OwnerKey,
    string State, BackupFile? Backup, long? SizeBytes = null, string? BackupSourceKey = null,
    bool IsTestContour = false, BackupFile? DtBackup = null);
public sealed record SqlDatabaseInfo(string Name, string State, long? SizeBytes = null, bool IsSnapshot = false);
public sealed record AuditEntry(long Id, string At, string Actor, string Action, string Detail);
public sealed record ConnectionResult(string ServerName, string Version, string Edition);

public sealed class InputException(string message) : Exception(message);
public sealed class SqlServiceException(string message, int? number = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int? Number { get; } = number;
}

public static partial class Rules
{
    [GeneratedRegex(@"^[a-z0-9]+(?:_[a-z0-9]+)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
    [GeneratedRegex(@"^[^\\/\s@]+\\[^\\/\s@]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
    [GeneratedRegex(@"^[a-zA-Z0-9_.-]{3,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AdminPattern();

    public static DatabaseName? ParseDatabase(string name)
    {
        const string marker = "_dev_";
        var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 && name.EndsWith("_tk", StringComparison.OrdinalIgnoreCase))
        {
            var testSource = name[..^3];
            return KeyPattern().IsMatch(testSource) ? new(testSource.ToLowerInvariant(), "", true) : null;
        }
        if (index < 1 || name.IndexOf(marker, index + marker.Length, StringComparison.OrdinalIgnoreCase) >= 0)
            return null;
        var source = name[..index];
        var owner = name[(index + marker.Length)..];
        return KeyPattern().IsMatch(source) && KeyPattern().IsMatch(owner)
            ? new(source.ToLowerInvariant(), owner.ToLowerInvariant()) : null;
    }

    public static string? OwnerFromIdentity(string identity)
    {
        if (!DomainPattern().IsMatch(identity)) return null;
        var account = identity[(identity.IndexOf('\\') + 1)..];
        return KeyPattern().IsMatch(account) ? account.ToLowerInvariant() : null;
    }

    public static bool Owns(string identity, string database)
    {
        var owner = OwnerFromIdentity(identity);
        var parsed = ParseDatabase(database);
        return parsed is { IsTestContour: false } && ContainsOwner(parsed, owner);
    }

    private static bool ContainsOwner(DatabaseName database, string? owner) => !string.IsNullOrEmpty(owner)
        && $"{database.SourceKey}_dev_{database.OwnerKey}".Contains(owner, StringComparison.OrdinalIgnoreCase);

    public static bool CanAccessDatabase(DatabaseName? database, AppUser user) => database is not null &&
        (user.IsAdmin || (database.IsTestContour
            ? user.Kind == "domain" && user.CanRefreshTestContour
            : ContainsOwner(database, user.OwnerKey)));

    public static string[] ValidateWindowsAdmins(string[]? names)
    {
        if(names?.Any(x=>x is null)==true)
            throw new InputException("Укажите точные учётные записи в формате DOMAIN\\user, по одной в строке.");
        var result = (names ?? []).Select(x => x.Trim()).Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length > 100 || result.Any(x => x.Length > 256 || !DomainPattern().IsMatch(x) || x.Contains('*') || x.Contains('?')))
            throw new InputException("Укажите точные учётные записи в формате DOMAIN\\user, по одной в строке.");
        return result;
    }

    public static void ValidateAdmin(string username, string password)
    {
        if (!AdminPattern().IsMatch(username))
            throw new InputException("Имя администратора: 3–64 латинских символа, цифры, точка, дефис или подчёркивание.");
        ValidatePassword(password);
    }

    public static void ValidatePassword(string password)
    {
        if (password.Length < 12 || password.Length > 256 || string.IsNullOrWhiteSpace(password))
            throw new InputException("Пароль должен содержать от 12 до 256 символов.");
    }

    public static void ValidateSettings(SettingsInput value)
    {
        if (string.IsNullOrWhiteSpace(value.Server) || value.Server.Length > 256 || value.Server.Any(char.IsControl))
            throw new InputException("Укажите адрес SQL Server, например DEV-SQL\\INSTANCE или DEV-SQL,1433.");
        if (value.Authentication is not ("sql" or "windows"))
            throw new InputException("Неизвестный способ подключения к SQL Server.");
        if (value.Authentication == "sql" && (string.IsNullOrWhiteSpace(value.Username) || value.Username.Length > 256))
            throw new InputException("Укажите имя SQL-пользователя.");
        if (value.Password?.Length > 1024) throw new InputException("Слишком длинный пароль SQL Server.");
        foreach (var path in new[] { value.BackupDirectory, value.SqlBackupDirectory })
            if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
                throw new InputException("Укажите абсолютные пути к каталогам бэкапов. Допускаются UNC-пути.");
    }

    public static object PublicSettings(SqlSettings? settings) => new
    {
        configured = settings is not null,
        server = settings?.Server ?? "",
        authentication = settings?.Authentication ?? "sql",
        username = settings?.Username ?? "",
        passwordConfigured = !string.IsNullOrEmpty(settings?.ProtectedPassword),
        backupDirectory = settings?.BackupDirectory ?? "",
        sqlBackupDirectory = settings?.SqlBackupDirectory ?? "",
        trustServerCertificate = settings?.TrustServerCertificate ?? false,
        runDbccCheckDb = settings?.RunDbccCheckDb ?? true,
        shrinkBackupDirectory = settings?.ShrinkBackupDirectory ?? "",
        sqlShrinkBackupDirectory = settings?.SqlShrinkBackupDirectory ?? "",
        copyBackupDirectory = settings?.CopyBackupDirectory ?? "",
        sqlCopyBackupDirectory = settings?.SqlCopyBackupDirectory ?? "",
        dtRestore = DtRestoreConfiguration.Public(settings?.DtRestore),
        oneC = settings?.OneC
    };
}
