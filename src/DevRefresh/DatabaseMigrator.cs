using Microsoft.Data.Sqlite;

namespace DevRefresh;

public sealed record SchemaMigration(int Version, string Name, string Sql);
public sealed record MigrationResult(int FromVersion, int ToVersion, string? BackupPath);

public static class DatabaseMigrator
{
    public const int LatestVersion = 14;
    public static IReadOnlyList<SchemaMigration> Migrations { get; } =
    [
        new(1, "Initial administration and settings", """
            CREATE TABLE local_admin (id INTEGER PRIMARY KEY CHECK(id=1), username TEXT NOT NULL,
                password_hash TEXT NOT NULL, version TEXT NOT NULL);
            CREATE TABLE windows_admins (name TEXT PRIMARY KEY COLLATE NOCASE);
            CREATE TABLE settings (id INTEGER PRIMARY KEY CHECK(id=1), value TEXT NOT NULL);
            CREATE TABLE audit (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, actor TEXT NOT NULL,
                action TEXT NOT NULL, detail TEXT NOT NULL);
            """),
        new(2, "Versioned schema migration history", """
            CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at_utc TEXT NOT NULL);
            INSERT INTO schema_migrations VALUES(1,'Initial administration and settings',strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """),
        new(3, "Reduction profiles and durable restore jobs", """
            CREATE TABLE reduction_profiles(source_key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE workflow_jobs(id TEXT PRIMARY KEY, kind TEXT NOT NULL, actor TEXT NOT NULL,
                target TEXT, source_key TEXT NOT NULL, status TEXT NOT NULL, stage TEXT NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL, request TEXT NOT NULL,
                result TEXT, error TEXT, resource_name TEXT, pinned_backup TEXT);
            CREATE INDEX workflow_jobs_status ON workflow_jobs(status,created_at);
            """),
        new(4, "Runtime corporate access settings", """
            CREATE TABLE access_settings(id INTEGER PRIMARY KEY CHECK(id=1), value TEXT NOT NULL);
            """),
        new(5, "Backup type aliases", """
            CREATE TABLE backup_aliases(target_type TEXT PRIMARY KEY NOT NULL, backup_type TEXT NOT NULL);
            """),
        new(6, "Application log retention", """
            CREATE TABLE log_retention (id INTEGER PRIMARY KEY CHECK(id=1), days INTEGER NOT NULL CHECK(days>0));
            """),
        new(7, "Scheduled database refresh", """
            ALTER TABLE workflow_jobs ADD COLUMN scheduled_at TEXT;
            CREATE INDEX workflow_jobs_scheduled ON workflow_jobs(scheduled_at) WHERE status='Scheduled';
            """),
        new(8, "Workflow execution reports", """
            ALTER TABLE workflow_jobs ADD COLUMN started_at TEXT;
            ALTER TABLE workflow_jobs ADD COLUMN execution_report TEXT;
            """),
        new(9, "Scheduled Shrink backups", """
            CREATE TABLE shrink_schedules(source_key TEXT PRIMARY KEY, value TEXT NOT NULL, last_slot TEXT);
            CREATE TABLE shrink_backups(job_id TEXT PRIMARY KEY, source_key TEXT NOT NULL, value TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('pending','current','retired')));
            CREATE UNIQUE INDEX shrink_current ON shrink_backups(source_key) WHERE state='current';
            CREATE INDEX shrink_jobs ON workflow_jobs(source_key,created_at DESC) WHERE kind='shrink-build';
            UPDATE workflow_jobs SET request=json_set(request,'$.BackupName','','$.Profile',NULL,'$.Rules',json('[]'),
                '$.SourceDatabase',NULL,'$.UsePreparedShrink',json('true'),'$.ResolveLatestBackup',json('true'))
            WHERE kind='refresh' AND status IN ('Scheduled','Queued') AND json_extract(request,'$.Shrink')=1;
            UPDATE workflow_jobs SET request=json_set(request,'$.BackupName','','$.Profile',NULL,'$.Rules',json('[]'),
                '$.ResolveLatestBackup',json('true'))
            WHERE kind='refresh' AND status IN ('Scheduled','Queued') AND json_extract(request,'$.SourceDatabase') IS NULL
              AND COALESCE(json_extract(request,'$.Shrink'),0)=0;
            """),
        new(10, "Developer database readiness and background maintenance", """
            ALTER TABLE workflow_jobs ADD COLUMN ready_at TEXT;
            ALTER TABLE workflow_jobs ADD COLUMN background_status TEXT;
            ALTER TABLE workflow_jobs ADD COLUMN background_error TEXT;
            """),
        // Older workers ignore UseDt in persisted JSON and could select a .bak instead.
        // Advance the compatibility version even though the SQLite tables are unchanged.
        new(11, "DT restore job format requires a compatible worker", "SELECT 1;"),
        new(12, "Restore operation pipelines by server, database and archive type", """
            CREATE TABLE restore_pipelines(server TEXT NOT NULL COLLATE NOCASE,database_name TEXT NOT NULL COLLATE NOCASE,
                source_kind TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(server,database_name,source_kind));
            """),
        new(13, "Restore operations by archive type and format; SQL-only IBCMD create", """
            ALTER TABLE restore_pipelines RENAME TO restore_pipelines_legacy;
            ALTER TABLE restore_pipelines_legacy ADD COLUMN migrated INTEGER NOT NULL DEFAULT 0;
            CREATE TABLE restore_pipelines(server TEXT NOT NULL COLLATE NOCASE,source_type TEXT NOT NULL COLLATE NOCASE,
                archive_format TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(server,source_type,archive_format));
            UPDATE settings SET value=json_remove(value,'$.DtRestore.InfobaseUser','$.DtRestore.ProtectedInfobasePassword') WHERE json_valid(value);
            UPDATE workflow_jobs SET request=json_remove(request,'$.Settings.DtRestore.InfobaseUser','$.Settings.DtRestore.ProtectedInfobasePassword','$.Pipeline') WHERE json_valid(request);
            """),
        new(14, "OAuth MCP restore plans and pinned source job format", """
            CREATE TABLE mcp_restore_plans(id TEXT PRIMARY KEY,owner TEXT NOT NULL,expires_at TEXT NOT NULL,value TEXT NOT NULL,
                job_id TEXT,request_id TEXT);
            CREATE UNIQUE INDEX mcp_restore_requests ON mcp_restore_plans(owner,request_id) WHERE request_id IS NOT NULL;
            """)
    ];

    public static SqliteConnection Connect(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 10 }.ToString());
        db.Open();
        return db;
    }

    public static int ReadVersion(string path)
    {
        if (!File.Exists(path)) return 0;
        using var db = Connect(path, SqliteOpenMode.ReadOnly);
        return Version(db);
    }

    private static int Version(SqliteConnection db, SqliteTransaction? transaction = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static void Backup(string source, string destination)
    {
        if (File.Exists(destination)) throw new IOException("Файл резервной копии уже существует.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var original = Connect(source, SqliteOpenMode.ReadOnly);
        using var backup = Connect(destination);
        original.BackupDatabase(backup);
        using var check = backup.CreateCommand();
        check.CommandText = "PRAGMA quick_check";
        if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
            throw new InvalidOperationException("Проверка резервной копии SQLite не пройдена.");
    }

    public static MigrationResult Ensure(string path, IReadOnlyList<SchemaMigration>? migrations = null)
    {
        migrations ??= Migrations;
        if (migrations.Count == 0 || !migrations.Select(x => x.Version).SequenceEqual(Enumerable.Range(1, migrations.Count)))
            throw new ArgumentException("Миграции должны иметь последовательные версии, начиная с 1.", nameof(migrations));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Connect(path);
        var from = Version(db);
        var latest = migrations[^1].Version;
        if (from > latest) throw new InvalidOperationException($"Схема SQLite версии {from} новее поддерживаемой {latest}. Понижение версии запрещено.");
        if (from == latest) return new(from, from, null);
        using var tables = db.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var hasData = Convert.ToInt32(tables.ExecuteScalar()) > 0;
        if (from == 0 && hasData) throw new InvalidOperationException("Неизвестная SQLite без номера схемы. Автоматическая миграция остановлена.");
        string? backupPath = null;
        if (hasData)
        {
            backupPath = Path.Combine(Path.GetDirectoryName(path)!, "migration-backups",
                $"schema-{from}-to-{latest}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite");
            Backup(path, backupPath);
        }
        using var transaction = db.BeginTransaction(deferred: false);
        var current = Version(db, transaction);
        if (current > latest) throw new InvalidOperationException("Схема была изменена другим процессом.");
        foreach (var migration in migrations.Where(x => x.Version > current))
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = migration.Sql;
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"PRAGMA user_version={migration.Version}";
            cmd.ExecuteNonQuery();
            if (migration.Version >= 2)
            {
                cmd.CommandText = "INSERT INTO schema_migrations(version,name,applied_at_utc) VALUES($version,$name,$at)";
                cmd.Parameters.AddWithValue("$version", migration.Version);
                cmd.Parameters.AddWithValue("$name", migration.Name);
                cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        using var journal = db.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode=WAL";
        journal.ExecuteNonQuery();
        return new(from, latest, backupPath);
    }
}

public sealed class RuntimeDataLock : IDisposable
{
    private FileStream? handle;
    private RuntimeDataLock(FileStream handle) => this.handle = handle;
    public static RuntimeDataLock Acquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        try { return new(new FileStream(Path.Combine(dataDirectory, ".devrefresh.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException ex) { throw new InvalidOperationException("Эта папка данных уже используется DevRefresh или обновлением. Сначала остановите другой экземпляр.", ex); }
    }
    public void Dispose() => Interlocked.Exchange(ref handle, null)?.Dispose();
}
