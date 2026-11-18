using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DevRefresh;

public sealed partial class Store
{
    public Dictionary<string, string> GetBackupAliases()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT target_type, backup_type FROM backup_aliases";
        using var reader = cmd.ExecuteReader();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            aliases.Add(reader.GetString(0), reader.GetString(1));
        return aliases;
    }

    public void SetBackupAlias(string targetType, string? backupType, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$target", targetType);
        if (backupType is null)
        {
            cmd.CommandText = "DELETE FROM backup_aliases WHERE target_type=$target";
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO backup_aliases(target_type, backup_type) VALUES($target, $backup)
                ON CONFLICT(target_type) DO UPDATE SET backup_type=excluded.backup_type
                """;
            cmd.Parameters.AddWithValue("$backup", backupType);
        }
        if (cmd.ExecuteNonQuery() > 0)
            WriteAudit(db, tx, actor, backupType is null ? "backup.alias.removed" : "backup.alias.saved",
                backupType is null ? $"Тип {targetType}: подбор по имени базы." : $"{targetType} → {backupType}");
        tx.Commit();
    }

    public ReductionProfile? GetProfile(string source)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT value FROM reduction_profiles WHERE source_key=$key";cmd.Parameters.AddWithValue("$key",source);
        return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize<ReductionProfile>(json):null;
    }
    public ReductionProfile[] GetProfiles()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT value FROM reduction_profiles ORDER BY source_key";
        using var reader=cmd.ExecuteReader();var result=new List<ReductionProfile>();
        while(reader.Read()) result.Add(JsonSerializer.Deserialize<ReductionProfile>(reader.GetString(0))!);
        return [..result];
    }
    public void SaveProfile(ReductionProfile profile)
    {
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO reduction_profiles VALUES($key,$value) ON CONFLICT(source_key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$key",profile.SourceKey);cmd.Parameters.AddWithValue("$value",JsonSerializer.Serialize(profile));cmd.ExecuteNonQuery();
        WriteAudit(db,tx,profile.UpdatedBy,"reduction.profile.saved",$"Тип {profile.SourceKey}: правил {profile.Rules.Length}, версия {profile.Revision}.");tx.Commit();
    }
    public WorkflowJob CreateJob(string kind,string actor,string source,string? target,WorkflowRequest request,DateTimeOffset? scheduledAt=null)
        => CreateJobCore(kind, actor, source, target, request, scheduledAt, null)!;

    internal WorkflowJob? TryQueueShrinkBuild(ShrinkSchedule schedule, WorkflowRequest request)
        => CreateJobCore("shrink-build", schedule.SavedBy, schedule.SourceKey, null, request, null, schedule);

    internal bool TrySkipShrinkBuild(ShrinkSchedule schedule, string slot)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        return TryAdvanceShrinkSlot(cmd, schedule, slot);
    }

    private static bool TryAdvanceShrinkSlot(SqliteCommand cmd, ShrinkSchedule schedule, string slot)
    {
        cmd.CommandText = """
            UPDATE shrink_schedules SET last_slot=$slot
            WHERE source_key=$source AND value=$schedule AND (last_slot IS NULL OR last_slot<$slot)
              AND NOT EXISTS (SELECT 1 FROM workflow_jobs WHERE kind='shrink-build' AND source_key=$source
                  AND status IN ('Queued','Running'))
            """;
        cmd.Parameters.AddWithValue("$slot", slot);
        cmd.Parameters.AddWithValue("$source", schedule.SourceKey);
        cmd.Parameters.AddWithValue("$schedule", JsonSerializer.Serialize(schedule));
        var advanced = cmd.ExecuteNonQuery() == 1;
        cmd.Parameters.Clear();
        return advanced;
    }

    private WorkflowJob? CreateJobCore(string kind,string actor,string source,string? target,WorkflowRequest request,
        DateTimeOffset? scheduledAt,ShrinkSchedule? schedule)
    {
        var now = DateTimeOffset.UtcNow;
        if (scheduledAt <= now) throw new InputException("Укажите дату и время запуска в будущем.");
        var id=Guid.NewGuid().ToString("N");var at=now.ToString("O");
        var scheduledUtc = scheduledAt?.ToUniversalTime().ToString("O");
        using var db=Open();using var tx=db.BeginTransaction(deferred:false);using var cmd=db.CreateCommand();cmd.Transaction=tx;
        if (schedule is not null)
        {
            if (!TryAdvanceShrinkSlot(cmd, schedule, request.BuildSlot!)) return null;
        }
        else if (kind=="shrink-build")
        {
            cmd.CommandText = """
                SELECT EXISTS (SELECT 1 FROM workflow_jobs WHERE kind='shrink-build' AND source_key=$source
                    AND status IN ('Queued','Running'))
                """;
            cmd.Parameters.AddWithValue("$source",source);
            if (Convert.ToInt32(cmd.ExecuteScalar()) != 0)
                throw new InputException("Для этого типа базы сборка Shrink-копии уже в очереди или выполняется.");
            cmd.Parameters.Clear();
        }
        if(target is not null)
        {
            cmd.CommandText = scheduledUtc is null
                ? """
                    SELECT COUNT(*) FROM workflow_jobs WHERE target=$target COLLATE NOCASE
                      AND (status='Queued' OR (status='Running' AND background_status IS NOT 'Queued'
                          AND background_status IS NOT 'Running'))
                    """
                : "SELECT COUNT(*) FROM workflow_jobs WHERE target=$target COLLATE NOCASE AND status='Scheduled' AND scheduled_at=$scheduled";
            cmd.Parameters.AddWithValue("$target",target);
            if (scheduledUtc is not null) cmd.Parameters.AddWithValue("$scheduled",scheduledUtc);
            if (Convert.ToInt32(cmd.ExecuteScalar()) > 0)
                throw new InputException(scheduledUtc is null
                    ? "Для этой базы уже есть задание в очереди или в работе."
                    : "Для этой базы уже есть задание на указанное время.");
            cmd.Parameters.Clear();
        }
        cmd.CommandText = """
            INSERT INTO workflow_jobs(id,kind,actor,target,source_key,status,stage,created_at,updated_at,request,scheduled_at)
            VALUES($id,$kind,$actor,$target,$source,$status,$stage,$at,$at,$request,$scheduled)
            """;
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$kind",kind);
        cmd.Parameters.AddWithValue("$actor",actor);
        cmd.Parameters.AddWithValue("$target",(object?)target??DBNull.Value);
        cmd.Parameters.AddWithValue("$source",source);
        cmd.Parameters.AddWithValue("$status",scheduledUtc is null?"Queued":"Scheduled");
        cmd.Parameters.AddWithValue("$stage",scheduledUtc is null?"В очереди":"Ожидает времени запуска");
        cmd.Parameters.AddWithValue("$at",at);
        cmd.Parameters.AddWithValue("$request",JsonSerializer.Serialize(request));
        cmd.Parameters.AddWithValue("$scheduled",(object?)scheduledUtc??DBNull.Value);
        cmd.ExecuteNonQuery();
        var auditDetail = $"Задание {id}, тип {source}, база {target??"служебная копия"}.";
        if(request.SourceDatabase is not null) auditDetail+=$" Источник {request.SourceDatabase}.";
        if (scheduledUtc is not null) auditDetail += $" Запуск {scheduledUtc} (UTC).";
        WriteAudit(db,tx,actor,kind+(scheduledUtc is null?".queued":".scheduled"),auditDetail);
        tx.Commit();
        return GetJob(id)!;
    }
    public WorkflowJob? GetJob(string id)=>GetJobsInternal("WHERE id=$id",id).SingleOrDefault();
    public WorkflowJob[] GetJobs()=>GetJobsInternal("ORDER BY (status='Scheduled') DESC, CASE WHEN status='Scheduled' THEN scheduled_at END, created_at DESC LIMIT 100",null);
    public WorkflowJob[] GetDashboardJobs(DateTimeOffset now)=>GetJobsInternal(
        "WHERE status IN('Scheduled','Queued','Running') OR (status IN('Succeeded','Failed','Interrupted') AND julianday(updated_at)>julianday($since)) ORDER BY created_at DESC",
        null,now.AddMinutes(-5).ToString("O"));
    public Dictionary<string,long> GetDashboardDurations(WorkflowJob[] jobs, AppUser user)
    {
        var estimates = new Dictionary<string,long>();
        var running = jobs.Where(j => j.Status == "Running" && j.StartedAt is not null && j.ReadyAt is null).ToArray();
        if (running.Length == 0) return estimates;

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(
                CAST(ROUND((julianday(ready_at)-julianday(started_at))*86400000) AS INTEGER),
                NULLIF(CASE WHEN json_valid(execution_report) THEN json_extract(execution_report,'$.DurationMs') END,0),
                CAST(ROUND((julianday(updated_at)-julianday(started_at))*86400000) AS INTEGER)
            ) AS duration_ms
            FROM workflow_jobs
            WHERE status='Succeeded' AND kind=$kind AND source_key=$source COLLATE NOCASE
              AND target IS $target COLLATE NOCASE
              AND ($shared=1 OR actor=$actor)
              AND julianday(updated_at)<=julianday($started)
              AND CASE WHEN json_valid(request) THEN json_extract(request,'$.Shrink') END=$shrink
              AND COALESCE(json_extract(request,'$.UsePreparedShrink'),0)=$prepared
              AND (CASE WHEN json_valid(request) THEN json_extract(request,'$.SourceDatabase') END IS NOT NULL)=$databaseCopy
              AND duration_ms>0
            ORDER BY updated_at DESC, created_at DESC
            LIMIT 7
            """;
        cmd.Parameters.AddWithValue("$actor",user.Name);
        var kind = cmd.Parameters.AddWithValue("$kind","");
        var source = cmd.Parameters.AddWithValue("$source","");
        var target = cmd.Parameters.AddWithValue("$target",DBNull.Value);
        var shared = cmd.Parameters.AddWithValue("$shared",0);
        var started = cmd.Parameters.AddWithValue("$started","");
        var shrink = cmd.Parameters.AddWithValue("$shrink",0);
        var prepared = cmd.Parameters.AddWithValue("$prepared",0);
        var databaseCopy = cmd.Parameters.AddWithValue("$databaseCopy",0);
        foreach (var job in running)
        {
            kind.Value = job.Kind;
            source.Value = job.SourceKey;
            target.Value = (object?)job.Target ?? DBNull.Value;
            var canReadTargetHistory = job.Target is not null
                && Rules.CanAccessDatabase(Rules.ParseDatabase(job.Target),user);
            shared.Value = user.IsAdmin || canReadTargetHistory ? 1 : 0;
            started.Value = job.StartedAt!.Value.ToString("O");
            shrink.Value = job.Request.Shrink ? 1 : 0;
            prepared.Value = job.Request.UsePreparedShrink ? 1 : 0;
            databaseCopy.Value = job.Request.SourceDatabase is null ? 0 : 1;
            var durations = new List<long>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) durations.Add(reader.GetInt64(0));
            if (durations.Count == 0) continue;
            durations.Sort();
            var middle = durations.Count / 2;
            estimates[job.Id] = durations.Count % 2 == 0
                ? durations[middle-1] + (durations[middle] - durations[middle-1]) / 2
                : durations[middle];
        }
        return estimates;
    }
    public WorkflowJob[] GetQueued()=>GetJobsInternal("WHERE status='Queued' ORDER BY COALESCE(scheduled_at,created_at),created_at",null);
    internal bool HasExecutingJobs()
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT EXISTS (SELECT 1 FROM workflow_jobs WHERE status='Running' AND background_status IS NOT 'Queued')";
        return Convert.ToInt32(cmd.ExecuteScalar())!=0;
    }
    internal WorkflowJob? GetNextBackgroundMaintenance()=>GetJobsInternal(
        "WHERE status='Running' AND background_status='Queued' ORDER BY ready_at,id LIMIT 1",null).SingleOrDefault();
    public string[] QueueDueJobs(DateTimeOffset now)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE workflow_jobs SET status='Queued',stage='В очереди',updated_at=$now
            WHERE status='Scheduled' AND scheduled_at<=$now
            RETURNING id,scheduled_at,created_at
            """;
        cmd.Parameters.AddWithValue("$now",now.ToUniversalTime().ToString("O"));
        var jobs = new List<(string Id,string ScheduledAt,string CreatedAt)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) jobs.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2)));
        return jobs.OrderBy(j=>j.ScheduledAt,StringComparer.Ordinal).ThenBy(j=>j.CreatedAt,StringComparer.Ordinal).Select(j=>j.Id).ToArray();
    }
    public bool TryStartJob(string id,DateTimeOffset now)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE workflow_jobs
            SET status=CASE WHEN scheduled_at>$now THEN 'Scheduled' ELSE 'Running' END,
                stage=CASE WHEN scheduled_at>$now THEN 'Ожидает времени запуска'
                    WHEN json_extract(request,'$.SourceDatabase') IS NOT NULL THEN 'Проверка базы назначения и соединений'
                    ELSE 'Открытие резервного файла' END,
                updated_at=$now,
                started_at=CASE WHEN scheduled_at>$now THEN NULL ELSE $now END
            WHERE id=$id AND status='Queued'
              AND NOT EXISTS (SELECT 1 FROM workflow_jobs AS active
                  WHERE active.status='Running' AND active.target=workflow_jobs.target COLLATE NOCASE)
            RETURNING status
            """;
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$now",now.ToUniversalTime().ToString("O"));
        return (string?)cmd.ExecuteScalar()=="Running";
    }
    public WorkflowJob[] GetCleanupJobs()=>GetJobsInternal("WHERE resource_name IS NOT NULL AND status NOT IN('Queued','Running')",null);
    internal void RegisterCopyBackup(string id,string filename,SqlSettings settings,string stage)
    {
        var job=GetJob(id) ?? throw new InvalidOperationException("Задание копирования не найдено.");
        using var db=Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            UPDATE workflow_jobs SET resource_name=$file,request=$request,stage=$stage
            WHERE id=$id AND status='Running' AND resource_name IS NULL
            """;
        cmd.Parameters.AddWithValue("$file",filename);
        cmd.Parameters.AddWithValue("$request",JsonSerializer.Serialize(job.Request with {Settings=settings}));
        cmd.Parameters.AddWithValue("$stage",stage);
        cmd.Parameters.AddWithValue("$id",id);
        if (cmd.ExecuteNonQuery()!=1) throw new InvalidOperationException("Не удалось зарегистрировать временный .bak.");
    }
    public void ClearResource(string id)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE workflow_jobs SET resource_name=NULL,pinned_backup=NULL WHERE id=$id";
        cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();
    }
    private WorkflowJob[] GetJobsInternal(string suffix,string? id,string? since=null)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,kind,actor,target,source_key,status,stage,created_at,updated_at,request,result,error,resource_name,pinned_backup,scheduled_at,started_at,ready_at,background_status,background_error FROM workflow_jobs "+suffix;
        if(id is not null)cmd.Parameters.AddWithValue("$id",id);
        if(since is not null)cmd.Parameters.AddWithValue("$since",since);
        using var r=cmd.ExecuteReader();var values=new List<WorkflowJob>();
        while(r.Read())values.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8),JsonSerializer.Deserialize<WorkflowRequest>(r.GetString(9))!,r.IsDBNull(10)?null:r.GetString(10),r.IsDBNull(11)?null:r.GetString(11),r.IsDBNull(12)?null:r.GetString(12),r.IsDBNull(13)?null:r.GetString(13),
            r.IsDBNull(14)?null:DateTimeOffset.ParseExact(r.GetString(14),"O",System.Globalization.CultureInfo.InvariantCulture),
            r.IsDBNull(15)?null:DateTimeOffset.ParseExact(r.GetString(15),"O",System.Globalization.CultureInfo.InvariantCulture),
            r.IsDBNull(16)?null:DateTimeOffset.ParseExact(r.GetString(16),"O",System.Globalization.CultureInfo.InvariantCulture),
            r.IsDBNull(17)?null:r.GetString(17),r.IsDBNull(18)?null:r.GetString(18)));
        return [..values];
    }
    public WorkflowExecutionReport? GetExecutionReport(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT execution_report FROM workflow_jobs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id",id);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<WorkflowExecutionReport>(json) : null;
    }
    public void UpdateJob(string id,string status,string stage,object? result=null,string? error=null,string? resource=null,string? pinned=null,
        WorkflowExecutionReport? executionReport=null)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE workflow_jobs SET status=$status,stage=$stage,updated_at=$at,result=COALESCE($result,result),error=$error,resource_name=COALESCE($resource,resource_name),pinned_backup=COALESCE($pinned,pinned_backup),execution_report=COALESCE($report,execution_report) WHERE id=$id";
        cmd.Parameters.AddWithValue("$report",executionReport is null?DBNull.Value:JsonSerializer.Serialize(executionReport));
        cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$status",status);cmd.Parameters.AddWithValue("$stage",stage);cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$result",result is null?DBNull.Value:JsonSerializer.Serialize(result));cmd.Parameters.AddWithValue("$error",(object?)error??DBNull.Value);cmd.Parameters.AddWithValue("$resource",(object?)resource??DBNull.Value);cmd.Parameters.AddWithValue("$pinned",(object?)pinned??DBNull.Value);cmd.ExecuteNonQuery();
    }
    public void InterruptRunningJobs()
    {
        using var db=Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            UPDATE workflow_jobs SET status='Succeeded',stage='База восстановлена',updated_at=$at,
                background_error=CASE WHEN background_status='Running' THEN 'Приложение перезапущено во время освобождения места.' ELSE background_error END,
                background_status=CASE WHEN background_status='Running' THEN 'Interrupted' ELSE background_status END
            WHERE status='Running' AND ready_at IS NOT NULL AND background_status IS NOT 'Queued';
            UPDATE workflow_jobs SET status='Interrupted',stage='Требуется проверка после перезапуска',
                error='Приложение перезапущено во время операции. Автоматический повтор восстановления не выполняется.',updated_at=$at
            WHERE status='Running' AND ready_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    internal void MarkDatabaseReady(string id,object result)
    {
        using var db=Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            UPDATE workflow_jobs SET ready_at=$at,updated_at=$at,stage='База готова к работе',result=$result
            WHERE id=$id AND kind='refresh' AND status='Running' AND ready_at IS NULL
            """;
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$result",JsonSerializer.Serialize(result));
        if (cmd.ExecuteNonQuery()!=1) throw new InvalidOperationException("Не удалось сохранить готовность базы.");
    }

    internal void QueueBackgroundMaintenance(string id,WorkflowRequest request,WorkflowExecutionReport report)
    {
        using var db=Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            UPDATE workflow_jobs SET background_status='Queued',stage='Освобождение места в очереди',updated_at=$at,
                request=$request,execution_report=$report
            WHERE id=$id AND status='Running' AND ready_at IS NOT NULL AND background_status IS NULL
            """;
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$at",report.FinishedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$request",JsonSerializer.Serialize(request));
        cmd.Parameters.AddWithValue("$report",JsonSerializer.Serialize(report));
        if (cmd.ExecuteNonQuery()!=1) throw new InvalidOperationException("Не удалось поставить освобождение места в очередь.");
    }

    internal void StartBackgroundMaintenance(string id,string stage)
    {
        using var db=Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            UPDATE workflow_jobs SET background_status='Running',stage=$stage,updated_at=$at
            WHERE id=$id AND status='Running' AND background_status='Queued'
            """;
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$stage",stage);
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery()!=1) throw new InvalidOperationException("Не удалось запустить освобождение места.");
    }

    internal void FinishBackgroundMaintenance(string id,string status,string? error,WorkflowExecutionReport report)
    {
        using var db=Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            UPDATE workflow_jobs SET status='Succeeded',stage='База восстановлена',background_status=$status,
                background_error=$error,updated_at=$at,execution_report=$report
            WHERE id=$id AND background_status='Running'
            """;
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$at",report.FinishedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$status",status);
        cmd.Parameters.AddWithValue("$error",(object?)error??DBNull.Value);
        cmd.Parameters.AddWithValue("$report",JsonSerializer.Serialize(report));
        cmd.ExecuteNonQuery();
    }
}
