using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevRefresh;

public sealed record RestoreSelection(string Mode, string SourceType, string SourceName, long? Size, DateTimeOffset? ModifiedAt,
    string ConfigurationHash, string Actor, string Subject);
public sealed record McpRestorePlan(string Id, string Owner, string Database, string RequestedMode, RestoreSelection Selection,
    string? FallbackReason, DateOnly? DataDate, int ActiveSessions, DateTimeOffset ExpiresAt, RestorePipeline? Pipeline)
{
    public object Public() => new
    {
        Status = "ready",
        PlanId = Id,
        Database,
        RequestedMode,
        EffectiveMode = Selection.Mode,
        Source = Selection.SourceName,
        SourceSize = Selection.Size,
        DataDate,
        FallbackReason,
        ActiveSessions,
        ExpiresAt,
        TargetWillBeDeleted = Selection.Mode == "dt",
        Operations = Pipeline?.Operations ?? [],
        DiskSpaceCheck = "not_implemented",
        ConfirmReplaceRequired = true,
        ConfirmDisconnectRequired = ActiveSessions > 0
    };
}
public sealed record McpSubmission(string PlanId, string Owner, string RequestId);

public sealed class McpPlans(Store store, ISqlGateway gateway, IRefreshInspector inspector, WorkflowService jobs, IConfiguration configuration)
{
    public static string Hash(SqlSettings settings, string sourceType, RestorePipeline? pipeline) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { settings, sourceType, pipeline }))));

    public async Task<object> Prepare(string database, string mode, string? sourceDatabase, AppUser user, string subject, string owner, CancellationToken token)
    {
        var parsed = WorkflowEndpoints.Authorize(database, user, configuration);
        if (mode is not ("shrink" or "full" or "dt" or "developer_copy")) throw new InputException("Источник: shrink, full, dt или developer_copy.");
        if (mode != "developer_copy" && sourceDatabase is not null) throw new InputException("База-источник задаётся только для developer_copy.");
        var settings = store.GetSettings() ?? throw new InputException("Подключение не настроено.");
        var databases = await gateway.List(settings, token);
        var target = databases.SingleOrDefault(x => x.Name.Equals(database, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.State != "ONLINE" || target.IsSnapshot) throw new InputException("База назначения не найдена или недоступна.");
        database = target.Name;
        var type = WorkflowEndpoints.ResolveBackupType(store, parsed.SourceKey);
        var sources = WorkflowEndpoints.CopySources(databases, database, parsed.SourceKey).ToArray();
        if (mode == "developer_copy" && string.IsNullOrEmpty(sourceDatabase))
            return new
            {
                Status = "source_required",
                Question = "Из какой именно базы другого разработчика копировать?",
                Database = database,
                Sources = sources.Select(x => new { x.Name, x.SizeBytes })
            };
        BackupFile? backup = null; string? fallback = null; var actual = mode;
        if (mode == "developer_copy")
        {
            if (DatabaseCopy.BackupDirectories(settings) is not { } paths || !Directory.Exists(paths.AppDirectory))
                throw new InputException("Не настроен доступный каталог временных копий Dev-баз.");
            sourceDatabase = sources.SingleOrDefault(x => x.Name.Equals(sourceDatabase, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new InputException("Выберите доступную Dev-базу того же типа из списка источников.");
        }
        else if (mode == "shrink")
        {
            var shrink = ShrinkBackups.Available(store, settings, parsed.SourceKey) ?? throw new InputException("Готовая Shrink-копия отсутствует. Выберите другой способ явно.");
            var info = new FileInfo(Path.Combine(settings.ShrinkBackupDirectory, shrink.Name));
            backup = new(shrink.Name, info.Length, info.LastWriteTimeUtc, type, shrink.DataDate);
        }
        else
        {
            (backup, actual) = SelectFullOrDt(settings, type, mode);
            if (mode != actual) fallback = mode == "full" ? "full_not_found" : "dt_not_found";
        }
        var pipeline = store.GetRestorePipeline(settings.Server, type, actual == "dt" ? "dt" : "bak");
        var selection = new RestoreSelection(actual, type, backup?.Name ?? sourceDatabase!, backup?.Size, backup?.ModifiedAt,
            Hash(settings, type, pipeline), user.Name, subject);
        ValidateSelection(store, configuration, database, selection);
        var plan = new McpRestorePlan(Guid.NewGuid().ToString("N"), owner, database, mode, selection, fallback, backup?.BackupDate,
            await inspector.Sessions(settings, database, token), DateTimeOffset.UtcNow.AddMinutes(5), pipeline);
        store.SaveMcpPlan(plan);
        return plan.Public();
    }

    public static (BackupFile Backup, string Mode) SelectFullOrDt(SqlSettings settings, string type, string requested)
    {
        (BackupFile? Backup, string? Error) Find(string mode)
        {
            var (files, error) = mode == "dt" ? DtRestoreConfiguration.Scan(settings) : BackupCatalog.Scan(settings.BackupDirectory);
            return (files.FirstOrDefault(x => x.SourceKey == type), error);
        }
        var preferred = Find(requested);
        if (preferred.Backup is { } found) return (found, requested);
        if (preferred.Error is not null) throw new InputException(preferred.Error);
        var alternativeMode = requested == "full" ? "dt" : "full";
        var alternative = Find(alternativeMode);
        if (alternative.Backup is { } other) return (other, alternativeMode);
        throw new InputException(alternative.Error ?? $"Для типа «{type}» нет ни Full-, ни .dt-архива.");
    }

    public async Task<object> Start(string planId, bool confirmReplace, bool confirmDisconnect, string requestId,
        AppUser user, string owner, CancellationToken token)
    {
        var plan = store.GetMcpPlan(planId, owner) ?? throw new InputException("План не найден или принадлежит другому подключению.");
        WorkflowEndpoints.Authorize(plan.Database, user, configuration);
        if (store.GetMcpPlanJob(planId, owner) is { } previous) return previous.Public();
        if (!confirmReplace) throw new InputException("Требуется явное указание пользователя заменить содержимое этой базы.");
        if (!Guid.TryParse(requestId, out var requestGuid)) throw new InputException("request_id должен быть UUID для защиты от повторного запуска.");
        requestId = requestGuid.ToString("D");
        if (plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new InputException("План истёк. Подготовьте восстановление заново.");
        ValidateSelection(store, configuration, plan.Database, plan.Selection);
        var settings = store.GetSettings()!;
        if (!confirmDisconnect && await inspector.Sessions(settings, plan.Database, token) > 0)
            throw new InputException("Есть активные соединения. Уточните у пользователя разрешение на их завершение.");
        var selection = plan.Selection;
        if (selection.Mode == "developer_copy")
        {
            var sources = WorkflowEndpoints.CopySources(await gateway.List(settings, token), plan.Database, Rules.ParseDatabase(plan.Database)!.SourceKey);
            if (!sources.Any(x => x.Name.Equals(selection.SourceName, StringComparison.OrdinalIgnoreCase))) throw new InputException("База-источник недоступна. Подготовьте новый план.");
        }
        var request = new WorkflowRequest(settings, selection.Mode == "developer_copy" ? "" : selection.SourceName, null, [], selection.Mode == "shrink", confirmDisconnect,
            SourceDatabase: selection.Mode == "developer_copy" ? selection.SourceName : null, UsePreparedShrink: selection.Mode == "shrink",
            UseDt: selection.Mode == "dt", Pipeline: plan.Pipeline, Selection: selection);
        var job = store.CreateMcpJob(user.Name, Rules.ParseDatabase(plan.Database)!.SourceKey, plan.Database, request, new(planId, owner, requestId));
        jobs.Enqueue(job);
        return job.Public();
    }

    public static void ValidateSelection(Store store, IConfiguration configuration, string database, RestoreSelection selection)
    {
        var settings = store.GetSettings() ?? throw new InputException("Подключение больше не настроено.");
        var access = store.GetWindowsAccess(configuration);
        var user = new AppUser(selection.Actor, "domain", store.GetWindowsAdmins().Contains(selection.Actor, StringComparer.OrdinalIgnoreCase),
            Rules.OwnerFromIdentity(selection.Actor), (access.TestContourOperators ?? []).Contains(selection.Actor, StringComparer.OrdinalIgnoreCase));
        if (!access.Enabled) throw new InputException("Доменный доступ отключён.");
        var parsed = WorkflowEndpoints.Authorize(database, user, configuration);
        var currentType = WorkflowEndpoints.ResolveBackupType(store, parsed.SourceKey);
        var pipeline = store.GetRestorePipeline(settings.Server, currentType, selection.Mode == "dt" ? "dt" : "bak");
        if (Hash(settings, currentType, pipeline) != selection.ConfigurationHash) throw new InputException("Настройки восстановления изменились. Подготовьте новый план.");
        if (selection.Mode == "developer_copy") return;
        var root = selection.Mode switch { "dt" => settings.DtRestore?.BackupDirectory, "shrink" => settings.ShrinkBackupDirectory, _ => settings.BackupDirectory };
        if (root is null || Path.GetFileName(selection.SourceName) != selection.SourceName) throw new InputException("Недопустимый архив плана.");
        var info = new FileInfo(Path.Combine(root, selection.SourceName));
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != selection.Size || info.LastWriteTimeUtc != selection.ModifiedAt?.UtcDateTime)
            throw new InputException("Выбранный архив изменился или исчез. Подготовьте новый план.");
    }
}

public sealed partial class Store
{
    public void SaveMcpPlan(McpRestorePlan plan)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM mcp_restore_plans WHERE expires_at<$old; INSERT INTO mcp_restore_plans(id,owner,expires_at,value) VALUES($id,$owner,$expires,$value)";
        command.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
        command.Parameters.AddWithValue("$id", plan.Id); command.Parameters.AddWithValue("$owner", plan.Owner);
        command.Parameters.AddWithValue("$expires", plan.ExpiresAt.ToString("O")); command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(plan)); command.ExecuteNonQuery();
    }
    public McpRestorePlan? GetMcpPlan(string id, string owner)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT value FROM mcp_restore_plans WHERE id=$id AND owner=$owner";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<McpRestorePlan>(json) : null;
    }
    public WorkflowJob? GetMcpPlanJob(string id, string owner)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT job_id FROM mcp_restore_plans WHERE id=$id AND owner=$owner";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner);
        return command.ExecuteScalar() is string job ? GetJob(job) : null;
    }
    public WorkflowJob CreateMcpJob(string actor, string source, string target, WorkflowRequest request, McpSubmission submission)
        => CreateJobCore("refresh", actor, source, target, request, null, null, submission)!;
}
