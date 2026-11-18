using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace DevRefresh;

public sealed class SessionService(Store store, IConfiguration configuration)
{
    public AppUser? Current(HttpContext context)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true) return null;
        var name = principal.Identity.Name;
        var kind = principal.FindFirstValue("kind");
        if (string.IsNullOrEmpty(name)) return null;
        if (kind == "local")
        {
            var admin = store.GetAdmin();
            return admin is not null && admin.Username == name && admin.Version == principal.FindFirstValue("version")
                ? new(name, kind, true, null) : null;
        }
        if (kind == "domain")
        {
            if(!store.GetWindowsAccess(configuration).Enabled)return null;
            return new(name, kind, store.GetWindowsAdmins().Contains(name, StringComparer.OrdinalIgnoreCase),
                Rules.OwnerFromIdentity(name),
                (store.GetWindowsAccess(configuration).TestContourOperators ?? []).Contains(name, StringComparer.OrdinalIgnoreCase));
        }
        return null;
    }

    public static Task SignIn(HttpContext context, string name, string kind, string? version = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name), new("kind", kind) };
        if (version is not null) claims.Add(new("version", version));
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
    }
}

public sealed class SettingsService(Store store, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("DevRefresh.SqlPassword.v1");

    public SqlSettings Prepare(SettingsInput input)
    {
        Rules.ValidateSettings(input);
        var previous = store.GetSettings();
        var password = "";
        if (input.Authentication == "sql")
        {
            if (!string.IsNullOrEmpty(input.Password)) password = protector.Protect(input.Password);
            else if (previous is not null && previous.Authentication == "sql"
                && string.Equals(previous.Server, input.Server.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.Username, input.Username.Trim(), StringComparison.Ordinal))
                password = previous.ProtectedPassword;
            else throw new InputException("Введите пароль SQL Server. При смене сервера или пользователя нужен новый пароль.");
            if (password.Length == 0) throw new InputException("Введите пароль SQL Server.");
        }
        var shrinkPath = input.ShrinkBackupDirectory?.Trim() ?? previous?.ShrinkBackupDirectory ?? "";
        var sqlShrinkPath = input.SqlShrinkBackupDirectory?.Trim() ?? previous?.SqlShrinkBackupDirectory ?? "";
        ShrinkBackups.ValidatePaths(shrinkPath, sqlShrinkPath, input.BackupDirectory.Trim(), input.SqlBackupDirectory.Trim());
        var copyPath = input.CopyBackupDirectory?.Trim() ?? previous?.CopyBackupDirectory ?? "";
        var sqlCopyPath = input.SqlCopyBackupDirectory?.Trim() ?? previous?.SqlCopyBackupDirectory ?? "";
        if ((copyPath.Length != 0 || sqlCopyPath.Length != 0) &&
            new[] {copyPath,sqlCopyPath}.Any(path => path.Length > 2048 || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl)))
            throw new InputException("Укажите оба абсолютных пути к папке временных .bak.");
        return new(input.Server.Trim(), input.Authentication, input.Authentication == "sql" ? input.Username.Trim() : "",
            password, input.BackupDirectory.Trim(), input.SqlBackupDirectory.Trim(), input.TrustServerCertificate,
            input.RunDbccCheckDb ?? previous?.RunDbccCheckDb ?? true, shrinkPath, sqlShrinkPath, copyPath, sqlCopyPath);
    }

    public string ConnectionString(SqlSettings value)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = value.Server, InitialCatalog = "master", ConnectTimeout = 5,
            Encrypt = SqlConnectionEncryptOption.Mandatory, TrustServerCertificate = value.TrustServerCertificate,
            IntegratedSecurity = value.Authentication == "windows", PersistSecurityInfo = false,
            ApplicationName = "DevRefresh", Pooling = true
        };
        if (value.Authentication == "sql")
        {
            builder.UserID = value.Username;
            try { builder.Password = protector.Unprotect(value.ProtectedPassword); }
            catch (CryptographicException)
            {
                throw new InputException("Не удалось расшифровать пароль. Введите его заново под текущей учётной записью службы.");
            }
        }
        return builder.ConnectionString;
    }
}

public interface ISqlGateway
{
    Task<ConnectionResult> Test(SqlSettings settings, CancellationToken token);
    Task<IReadOnlyList<SqlDatabaseInfo>> List(SqlSettings settings, CancellationToken token);
}

public sealed class SqlGateway(SettingsService settingsService, ILogger<SqlGateway>? logger = null) : ISqlGateway
{
    public async Task<ConnectionResult> Test(SqlSettings settings, CancellationToken token)
    {
        try
        {
            await using var connection = new SqlConnection(settingsService.ConnectionString(settings));
            await connection.OpenAsync(token);
            await using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = 10;
            cmd.CommandText = "SELECT CONVERT(nvarchar(256),SERVERPROPERTY('ServerName')), CONVERT(nvarchar(256),SERVERPROPERTY('ProductVersion')), CONVERT(nvarchar(256),SERVERPROPERTY('Edition'))";
            await using var row = await cmd.ExecuteReaderAsync(token);
            await row.ReadAsync(token);
            return new(row.IsDBNull(0) ? settings.Server : row.GetString(0), row.GetString(1), row.GetString(2));
        }
        catch (SqlException ex) { throw ConvertError(ex); }
    }

    public async Task<IReadOnlyList<SqlDatabaseInfo>> List(SqlSettings settings, CancellationToken token)
    {
        try
        {
            await using var connection = new SqlConnection(settingsService.ConnectionString(settings));
            await connection.OpenAsync(token);
            await using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = 10;
            cmd.CommandText = """
                SELECT d.name,d.state_desc,SUM(CONVERT(bigint,f.size)*8192) AS size_bytes,d.source_database_id
                FROM sys.databases AS d
                LEFT JOIN sys.master_files AS f ON f.database_id=d.database_id
                WHERE d.database_id > 4
                GROUP BY d.database_id,d.name,d.state_desc,d.source_database_id
                ORDER BY d.name
                """;
            await using var rows = await cmd.ExecuteReaderAsync(token);
            var result = new List<SqlDatabaseInfo>();
            while (await rows.ReadAsync(token)) result.Add(new(rows.GetString(0), rows.GetString(1),rows.IsDBNull(2)?null:rows.GetInt64(2),!rows.IsDBNull(3)));
            return result;
        }
        catch (SqlException ex) { throw ConvertError(ex); }
    }

    private SqlServiceException ConvertError(SqlException ex)
    {
        logger?.LogWarning("SQL connection failure. Number {SqlNumber}; State {SqlState}; Class {SqlClass}; HResult {SqlHResult}; ConnectionId {SqlConnectionId}; InnerType {InnerType}; InnerHResult {InnerHResult}",
            ex.Number, ex.State, ex.Class, ex.HResult, ex.ClientConnectionId, ex.InnerException?.GetType().FullName, ex.InnerException?.HResult);
        return new(ex.Number switch
        {
            18456 => "SQL Server отклонил вход. Проверьте способ аутентификации, логин и пароль.",
            -2 => "SQL Server не ответил вовремя. Проверьте доступность сервера и сети.",
            4060 or 916 => "У подключения недостаточно прав на SQL Server.",
            _ => "Не удалось подключиться к SQL Server. Проверьте адрес, сетевой доступ, сертификат и права службы."
        }, ex.Number);
    }
}

public static class BackupCatalog
{
    public static (BackupFile[] Files, string? Error) Scan(string directory)
    {
        var files = new List<BackupFile>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (WorkflowRules.ParseBackupName(Path.GetFileName(path)) is not { } parsed) continue;
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                files.Add(new(info.Name, info.Length, new DateTimeOffset(info.LastWriteTimeUtc), parsed.SourceKey, parsed.BackupDate));
            }
            return (files.OrderByDescending(file => file.BackupDate)
                .ThenByDescending(file => file.ModifiedAt)
                .ThenBy(file => file.Name, StringComparer.Ordinal).ToArray(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], "Не удалось прочитать каталог бэкапов. Администратору нужно проверить путь и права службы.");
        }
    }

    public static BackupFile RequireLatest(string directory, string sourceKey)
    {
        var (files, error) = Scan(directory);
        return files.FirstOrDefault(file => file.SourceKey == sourceKey)
            ?? throw new InputException(error ?? $"В каталоге нет резервного файла для типа базы «{sourceKey}».");
    }
}
