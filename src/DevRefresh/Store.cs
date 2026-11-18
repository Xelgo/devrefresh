using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DevRefresh;

public sealed partial class Store
{
    private const int DefaultLogRetentionDays = 14;
    private readonly string connectionString;
    public Store(string path)
    {
        DatabaseMigrator.Ensure(path);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 10, Pooling = false }.ToString();
        MigrateLegacyRestorePipelines();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    public LocalAdmin? GetAdmin()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT username,password_hash,version FROM local_admin WHERE id=1";
        using var rows = cmd.ExecuteReader();
        return rows.Read() ? new(rows.GetString(0), rows.GetString(1), rows.GetString(2)) : null;
    }

    public bool Initialize(LocalAdmin admin, string[] accounts)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT OR IGNORE INTO local_admin VALUES(1,$username,$hash,$version)";
        cmd.Parameters.AddWithValue("$username", admin.Username);
        cmd.Parameters.AddWithValue("$hash", admin.PasswordHash);
        cmd.Parameters.AddWithValue("$version", admin.Version);
        if (cmd.ExecuteNonQuery() != 1) return false;
        ReplaceWindowsAdmins(db, transaction, accounts);
        WriteAudit(db, transaction, admin.Username, "setup.completed", "Первый администратор создан.");
        transaction.Commit();
        return true;
    }

    public string[] GetWindowsAdmins()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM windows_admins ORDER BY name";
        using var rows = cmd.ExecuteReader();
        var result = new List<string>();
        while (rows.Read()) result.Add(rows.GetString(0));
        return [.. result];
    }

    private static void ReplaceWindowsAdmins(SqliteConnection db, SqliteTransaction transaction, string[] names)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM windows_admins";
        cmd.ExecuteNonQuery();
        foreach (var name in names)
        {
            cmd.CommandText = "INSERT INTO windows_admins(name) VALUES($name)";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$name", name);
            cmd.ExecuteNonQuery();
        }
    }

    public SqlSettings? GetSettings()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE id=1";
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<SqlSettings>(json);
    }

    public void SaveSettings(SqlSettings settings, string actor)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO settings VALUES(1,$value) ON CONFLICT(id) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(settings));
        cmd.ExecuteNonQuery();
        WriteAudit(db, transaction, actor, "settings.saved", "Параметры SQL Server и каталогов обновлены.");
        transaction.Commit();
    }

    public int GetLogRetentionDays()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT days FROM log_retention WHERE id=1";
        return cmd.ExecuteScalar() is long days ? (int)days : DefaultLogRetentionDays;
    }

    public void SaveLogRetentionDays(int days, string actor)
    {
        if (days < 1) throw new InputException("Срок хранения журналов должен быть целым числом от 1 дня.");
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO log_retention(id,days) VALUES(1,$days) ON CONFLICT(id) DO UPDATE SET days=excluded.days";
        cmd.Parameters.AddWithValue("$days", days);
        cmd.ExecuteNonQuery();
        WriteAudit(db, transaction, actor, "log.retention.saved", $"Установлен срок хранения файлов журнала — {days} дн.");
        transaction.Commit();
    }

    public WindowsAccess GetWindowsAccess(IConfiguration configuration)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT value FROM access_settings WHERE id=1";
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<WindowsAccess>(json)!
            : new(configuration.GetValue("WindowsAuthentication:Enabled",true),
                configuration.GetValue("WindowsAuthentication:AutoSignIn",false));
    }

    public void SaveSecurity(string[] accounts, LocalAdmin? replacement, string actor, WindowsAccess? access=null)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        ReplaceWindowsAdmins(db, transaction, accounts);
        if(access is not null)
        {
            using var accessCommand=db.CreateCommand();accessCommand.Transaction=transaction;
            accessCommand.CommandText="INSERT INTO access_settings VALUES(1,$value) ON CONFLICT(id) DO UPDATE SET value=excluded.value";
            accessCommand.Parameters.AddWithValue("$value",JsonSerializer.Serialize(access));accessCommand.ExecuteNonQuery();
            WriteAudit(db,transaction,actor,"access.saved",access.Enabled
                ? $"Корпоративный вход включён; автоматический вход: {access.AutoSignIn}."
                : "Корпоративный вход отключён.");
        }
        if (replacement is not null)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE local_admin SET password_hash=$hash,version=$version WHERE id=1";
            cmd.Parameters.AddWithValue("$hash", replacement.PasswordHash);
            cmd.Parameters.AddWithValue("$version", replacement.Version);
            cmd.ExecuteNonQuery();
        }
        WriteAudit(db, transaction, actor, "security.saved", replacement is null
            ? "Списки доменных администраторов и операторов тестового контура обновлены."
            : "Списки доступа и пароль обновлены; старые локальные сессии отозваны.");
        transaction.Commit();
    }

    public void Audit(string actor, string action, string detail)
    {
        using var db = Open();
        WriteAudit(db, null, actor, action, detail);
    }

    private static void WriteAudit(SqliteConnection db, SqliteTransaction? transaction, string actor, string action, string detail)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO audit(at,actor,action,detail) VALUES($at,$actor,$action,$detail)";
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$actor", actor);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$detail", detail);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditEntry> GetAudit()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,at,actor,action,detail FROM audit ORDER BY id DESC LIMIT 100";
        using var rows = cmd.ExecuteReader();
        var result = new List<AuditEntry>();
        while (rows.Read()) result.Add(new(rows.GetInt64(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4)));
        return result;
    }
}
