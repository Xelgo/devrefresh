namespace DevRefresh;

public interface IRefreshInspector
{
    Task<int> Sessions(SqlSettings settings, string database, CancellationToken token);
}
public sealed class RefreshInspector(RestoreSql sql) : IRefreshInspector
{
    public Task<int> Sessions(SqlSettings settings, string database, CancellationToken token) => sql.Sessions(settings, database, token);
}

public sealed class RefreshOperations(Store store, ISqlGateway gateway, WorkflowService jobs, IConfiguration configuration, IRefreshInspector inspector)
{
    public DatabaseName Authorize(string database, AppUser user) => WorkflowEndpoints.Authorize(database, user, configuration);
    public async Task<object> Databases(AppUser user, CancellationToken token)
    {
        var settings = store.GetSettings();
        if (settings is null) return new { Configured = false, Databases = Array.Empty<DatabaseEntry>(), BackupError = (string?)null };
        var databases = await gateway.List(settings, token);
        var allowed = configuration.GetSection("TestContour:AllowedTargets").Get<string[]>();
        var visible = databases.Select(x => (Database: x, Parsed: Rules.ParseDatabase(x.Name)))
            .Where(x => Rules.CanAccessDatabase(x.Parsed, user) && (allowed is not { Length: > 0 } || allowed.Contains(x.Database.Name, StringComparer.OrdinalIgnoreCase)));
        var (backups, error) = BackupCatalog.Scan(settings.BackupDirectory);
        var (dtFiles, _) = DtRestoreConfiguration.Scan(settings);
        var aliases = store.GetBackupAliases();
        var entries = visible.Select(x =>
        {
            var parsed = x.Parsed!; var type = aliases.GetValueOrDefault(parsed.SourceKey, parsed.SourceKey);
            return new DatabaseEntry(x.Database.Name, parsed.SourceKey, parsed.OwnerKey, x.Database.State,
                backups.FirstOrDefault(b => b.SourceKey == type), x.Database.SizeBytes, type, parsed.IsTestContour, dtFiles.FirstOrDefault(b => b.SourceKey == type));
        }).ToArray();
        return new { Configured = true, Databases = entries, BackupError = error };
    }
    public async Task<object> Options(string database, AppUser user, CancellationToken token)
    {
        var parsed = Authorize(database, user);
        var settings = store.GetSettings() ?? throw new InputException("Подключение не настроено.");
        var backupType = WorkflowEndpoints.ResolveBackupType(store, parsed.SourceKey);
        var (backups, backupError) = BackupCatalog.Scan(settings.BackupDirectory);
        var backup = backups.FirstOrDefault(b => b.SourceKey == backupType);
        var sources = WorkflowEndpoints.CopySources(await gateway.List(settings, token), database, parsed.SourceKey);
        var ready = ShrinkBackups.Available(store, settings, parsed.SourceKey);
        var (dtFiles, dtError) = DtRestoreConfiguration.Scan(settings);
        var dtBackup = dtFiles.FirstOrDefault(x => x.SourceKey == backupType);
        return new
        {
            Database = database,
            SourceKey = parsed.SourceKey,
            BackupName = backup?.Name,
            BackupDate = backup?.BackupDate,
            BackupModifiedAt = backup?.ModifiedAt,
            BackupSize = backup?.Size,
            BackupUnavailableReason = backup is null ? backupError ?? $"В каталоге нет резервного файла для типа базы «{backupType}»." : null,
            CopySources = sources.Select(x => new { x.Name, x.SizeBytes }),
            CopyBackupConfigured = DatabaseCopy.BackupDirectories(settings) is not null,
            ShrinkBackup = ready,
            ShrinkConfigured = ShrinkBackups.Configured(settings),
            DtBackup = dtBackup,
            DtConfigured = DtRestoreConfiguration.UnavailableReason(settings) is null,
            DtUnavailableReason = dtBackup is null ? dtError ?? $"В каталоге .dt нет архива для типа базы «{backupType}»." : null,
            ActiveSessions = await inspector.Sessions(settings, database, token),
            PersonalDataAvailable = false
        };

    }
    public async Task<WorkflowJob> Create(RefreshInput input, AppUser user, CancellationToken token)
    {
        var parsed = Authorize(input.Database, user);
        if (!input.ConfirmReplace) throw new InputException("Подтвердите замену текущего содержимого базы.");
        if (input.Pii) throw new InputException("Обработка персональных данных ещё не включена. В этом контуре проверяется только сокращение.");
        var settings = store.GetSettings() ?? throw new InputException("Не настроено подключение.");
        var backupType = WorkflowEndpoints.ResolveBackupType(store, parsed.SourceKey);
        if (input.Dt)
        {
            if (input.Shrink || input.SourceDatabase is not null || input.BackupName is not null || input.Rules is { Length: > 0 })
                throw new InputException("Выберите один источник: .dt используется отдельно от Full, Shrink и другой Dev-базы.");
            if (DtRestoreConfiguration.UnavailableReason(settings) is { } reason) throw new InputException(reason);
            if (input.ScheduledAt is null) DtRestoreConfiguration.RequireLatest(settings, backupType);
            var dtJob = store.CreateJob("refresh", user.Name, parsed.SourceKey, input.Database,
                new(settings, "", null, [], false, input.ConfirmDisconnect, ResolveLatestBackup: true, UseDt: true), input.ScheduledAt);
            jobs.Enqueue(dtJob); return dtJob;
        }
        if (input.Shrink)
        {
            if (!ShrinkBackups.Configured(settings))
                throw new InputException("Каталог Shrink-копий ещё не настроен.");
            if (input.SourceDatabase is not null || input.BackupName is not null || input.Rules is { Length: > 0 })
                throw new InputException("Готовая Shrink-копия используется как отдельный источник с сохранённым профилем.");
            if (ShrinkBackups.Available(store, settings, parsed.SourceKey) is null && input.ScheduledAt is null)
                throw new InputException("Для этого типа пока нет готовой Shrink-копии.");
            var prepared = store.CreateJob("refresh", user.Name, parsed.SourceKey, input.Database,
                new(settings, "", null, [], true, input.ConfirmDisconnect, UsePreparedShrink: true, ResolveLatestBackup: true), input.ScheduledAt);
            jobs.Enqueue(prepared);
            return prepared;
        }
        if (input.Rules is { Length: > 0 })
            throw new InputException("Выберите один источник актуализации.");
        string? sourceDatabase = null;
        if (input.SourceDatabase is not null)
        {
            if (input.BackupName is not null || !WorkflowRules.IsCopySource(input.SourceDatabase, input.Database, parsed.SourceKey))
                throw new InputException("Выберите один источник. Подходит резервный файл или другая Dev-база того же типа.");
            var databases = await gateway.List(settings, token);
            sourceDatabase = WorkflowEndpoints.CopySources(databases, input.Database, parsed.SourceKey)
                .FirstOrDefault(x => x.Name.Equals(input.SourceDatabase, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new InputException("База-источник не найдена или сейчас недоступна.");
        }
        if (sourceDatabase is null && input.ScheduledAt is null)
            BackupCatalog.RequireLatest(settings.BackupDirectory, backupType);
        var job = store.CreateJob("refresh", user.Name, parsed.SourceKey, input.Database,
            new(settings, "", null, [], false, input.ConfirmDisconnect, sourceDatabase, ResolveLatestBackup: sourceDatabase is null), input.ScheduledAt);
        jobs.Enqueue(job);
        return job;

    }
}
