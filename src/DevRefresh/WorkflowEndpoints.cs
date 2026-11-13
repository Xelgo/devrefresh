namespace DevRefresh;

public static class WorkflowEndpoints
{
    public static void MapWorkflow(this WebApplication app)
    {
        app.MapGet("/api/admin/reduction/catalog",async(Store store,ISqlGateway sql,CancellationToken token)=>
        {
            var settings=store.GetSettings();if(settings is null)throw new InputException("Сначала настройте SQL Server и каталоги бэкапов.");
            var(files,error)=BackupCatalog.Scan(settings.BackupDirectory);
            var databases=await sql.List(settings,token);
            var profiles=store.GetProfiles();
            var aliases=store.GetBackupAliases();
            var schedules=store.GetShrinkSchedules();
            var devTypes=databases.Select(d=>Rules.ParseDatabase(d.Name)?.SourceKey).OfType<string>()
                .Concat(profiles.Select(p=>p.SourceKey)).Concat(aliases.Keys).Concat(schedules.Select(s=>s.SourceKey)).Distinct().Order().ToArray();
            var types=devTypes.Concat(files.Select(f=>f.SourceKey)).Distinct().Order().ToArray();
            var currentProfiles=profiles.Where(p=>WorkflowRules.ProfileMatchesBackup(p,aliases.GetValueOrDefault(p.SourceKey,p.SourceKey)));
            return Results.Ok(new {Types=types,DevTypes=devTypes,Backups=files,BackupAliases=aliases,Profiles=currentProfiles,BackupError=error,
                ShrinkSchedules=schedules,ShrinkDirectoryConfigured=ShrinkBackups.Configured(settings),
                ScheduleTimeZone=TimeZoneInfo.Local.DisplayName,
                ShrinkBackups=devTypes.Select(type=>ShrinkBackups.Available(store,settings,type)).OfType<ShrinkBackup>(),
                ShrinkBuilds=store.GetLatestShrinkBuilds().Select(j=>j.Public()),
                Analyses=store.GetJobs().Where(j=>j.Kind=="analysis").Select(j=>j.Public())});
        });
        app.MapPut("/api/admin/reduction/shrink-schedules/{source}",(string source,ShrinkScheduleInput input,
            HttpContext context,Store store,SessionService sessions)=>
        {
            source=WorkflowRules.Source(source);
            if (input.Enabled)
                RequireShrinkBuildSettings(store,source);
            var schedule=ShrinkBackups.CreateSchedule(source,input,sessions.Current(context)!.Name,DateTimeOffset.UtcNow);
            store.SaveShrinkSchedule(schedule);
            return Results.Ok(schedule);
        });
        app.MapPost("/api/admin/reduction/shrink-builds/{source}",(string source,HttpContext context,
            Store store,SessionService sessions,WorkflowService jobs)=>
        {
            source=WorkflowRules.Source(source);
            var (settings,profile)=RequireShrinkBuildSettings(store,source);
            BackupCatalog.RequireLatest(settings.BackupDirectory,ResolveBackupType(store,source));
            var job=store.CreateJob("shrink-build",sessions.Current(context)!.Name,source,null,
                new(settings,"",profile,profile.Rules,true,false));
            jobs.Enqueue(job);
            return Results.Accepted("/api/jobs/"+job.Id,job.Public());
        });
        app.MapPut("/api/admin/reduction/backup-aliases/{target}",(string target,BackupAliasInput input,HttpContext context,Store store,SessionService sessions)=>
        {
            var targetType=WorkflowRules.Source(target);
            var backupType=WorkflowRules.Source(input.BackupSourceKey);
            if(targetType==backupType)throw new InputException("Для одинаковых типов сайт подбирает бэкап по имени базы. Удалите явное соответствие.");
            store.SetBackupAlias(targetType,backupType,sessions.Current(context)!.Name);
            return Results.Ok(new {TargetType=targetType,BackupSourceKey=backupType});
        });
        app.MapDelete("/api/admin/reduction/backup-aliases/{target}",(string target,HttpContext context,Store store,SessionService sessions)=>
        {
            var targetType=WorkflowRules.Source(target);
            store.SetBackupAlias(targetType,null,sessions.Current(context)!.Name);
            return Results.Ok(new {TargetType=targetType});
        });
        app.MapPost("/api/admin/reduction/analyses",(AnalysisInput input,HttpContext context,Store store,SessionService sessions,WorkflowService jobs)=>
        {
            var settings=store.GetSettings()??throw new InputException("Не настроено подключение.");
            var(files,error)=BackupCatalog.Scan(settings.BackupDirectory);
            var backup=files.FirstOrDefault(f=>f.Name==input.BackupName)
                ??throw new InputException(error??"Выбранный бэкап отсутствует в каталоге.");
            var job=store.CreateJob("analysis",sessions.Current(context)!.Name,backup.SourceKey,null,new(settings,backup.Name,null,[],false,false));jobs.Enqueue(job);
            return Results.Accepted("/api/jobs/"+job.Id,job.Public());
        });
        app.MapGet("/api/admin/reduction/analyses/{id}",(string id,Store store,WorkflowService jobs)=>
        {
            var job=store.GetJob(id);if(job is null||job.Kind!="analysis"||job.Status!="Succeeded")throw new InputException("Анализ ещё не завершён.");
            var data=jobs.ReadAnalysis(id);
            return Results.Ok(new {data.Id,data.SourceKey,data.BackupName,data.Configuration,data.ConfigurationVersion,data.AnalyzedAt,data.Warnings,
                Tables=data.Tables.Select(t=>new {t.Key,t.LogicalName,t.Schema,t.Table,t.Kind,t.RootKey,t.Role,t.CanSelect,t.Reason,t.DateColumn,t.DateParentKey,t.YearOffset,t.Rows,t.DataMB,t.IndexMB,t.UsedMB,t.ReservedMB})});
        });
        app.MapPut("/api/admin/reduction/profiles/{source}",(string source,ProfileInput input,HttpContext context,Store store,SessionService sessions,WorkflowService jobs)=>
        {
            source=WorkflowRules.Source(source);var job=store.GetJob(input.AnalysisId);
            if(job is null||job.Status!="Succeeded"||job.Kind!="analysis"||job.SourceKey!=ResolveBackupType(store,source))
                throw new InputException("Выберите завершённый анализ бэкапа, соответствующего этому типу Dev-базы.");
            var analysis=jobs.ReadAnalysis(input.AnalysisId);var rules=WorkflowRules.ValidateSelection(analysis,input.Rules);
            var profile=new ReductionProfile(source,input.AnalysisId,analysis.BackupName,Guid.NewGuid().ToString("N"),rules,DateTimeOffset.UtcNow.ToString("O"),sessions.Current(context)!.Name);
            store.SaveProfile(profile);return Results.Ok(profile);
        });
        app.MapPost("/api/admin/reduction/preview",(ProfileInput input,HttpContext context,Store store,SessionService sessions,WorkflowService jobs)=>
        {
            var job=store.GetJob(input.AnalysisId);
            if(job is null||job.Kind!="analysis"||job.Status!="Succeeded")throw new InputException("Завершённый анализ не найден.");
            var analysis=jobs.ReadAnalysis(input.AnalysisId);var rules=WorkflowRules.ValidateSelection(analysis,input.Rules);
            var settings=store.GetSettings()??throw new InputException("Не настроено подключение.");
            var profile=new ReductionProfile(job.SourceKey,job.Id,analysis.BackupName,"preview",rules,DateTimeOffset.UtcNow.ToString("O"),sessions.Current(context)!.Name);
            var preview=store.CreateJob("preview",profile.UpdatedBy,job.SourceKey,null,new(settings,analysis.BackupName,profile,rules,true,false));
            jobs.Enqueue(preview);return Results.Accepted("/api/jobs/"+preview.Id,preview.Public());
        });
        app.MapPost("/api/admin/reduction/analyses/{id}/release",async(string id,Store store,RestoreSql sql,CancellationToken token)=>
        {
            var job=store.GetJob(id);
            if(job is null||job.Kind!="analysis"||job.Status is "Queued" or "Running"||job.ResourceName is null)throw new InputException("Служебная копия недоступна для освобождения.");
            await sql.DropOwned(job.Request.Settings,job.ResourceName,job.Id,token);
            return Results.Ok(new {ok=true});
        });
        app.MapGet("/api/refresh/options",async(string database,HttpContext context,Store store,SessionService sessions,RestoreSql sql,ISqlGateway gateway,CancellationToken token)=>
        {
            var user=sessions.Current(context)!;var parsed=Authorize(database,user,app.Configuration);
            var settings=store.GetSettings()??throw new InputException("Подключение не настроено.");
            var backupType=ResolveBackupType(store,parsed.SourceKey);
            var (backups,backupError)=BackupCatalog.Scan(settings.BackupDirectory);
            var backup=backups.FirstOrDefault(b=>b.SourceKey==backupType);
            var sources=CopySources(await gateway.List(settings,token),database,parsed.SourceKey);
            var ready=ShrinkBackups.Available(store,settings,parsed.SourceKey);
            return Results.Ok(new {Database=database,SourceKey=parsed.SourceKey,BackupName=backup?.Name,
                BackupDate=backup?.BackupDate,BackupModifiedAt=backup?.ModifiedAt,
                BackupUnavailableReason=backup is null ? backupError ?? $"В каталоге нет резервного файла для типа базы «{backupType}»." : null,
                CopySources=sources.Select(x=>new {x.Name,x.SizeBytes}),
                ShrinkBackup=ready,ShrinkConfigured=ShrinkBackups.Configured(settings),
                ActiveSessions=await sql.Sessions(settings,database,token),PersonalDataAvailable=false});
        });
        app.MapPost("/api/refresh/estimate",(ReductionEstimateInput input,HttpContext context,Store store,SessionService sessions,WorkflowService jobs)=>
        {
            var parsed=Authorize(input.Database,sessions.Current(context)!,app.Configuration);
            var profile=store.GetProfile(parsed.SourceKey);
            if(profile is null||profile.Revision!=input.ProfileRevision)throw new InputException("Профиль изменился. Откройте окно актуализации заново.");
            if(input.SourceDatabase is not null && !WorkflowRules.IsCopySource(input.SourceDatabase,input.Database,parsed.SourceKey))
                throw new InputException("Выберите другую Dev-базу того же типа.");
            if(input.SourceDatabase is null && !WorkflowRules.ProfileMatchesBackup(profile,ResolveBackupType(store,parsed.SourceKey)))
                throw new InputException("Источник бэкапа изменился. Администратору нужно обновить профиль сокращения.");
            var analysis=jobs.ReadAnalysis(profile.AnalysisId);
            var rules=WorkflowRules.ValidateSelection(analysis,input.Rules,profile.Rules.Select(r=>r.Key).ToHashSet(StringComparer.Ordinal));
            return Results.Ok(ReductionEstimator.Calculate(analysis,rules));
        });
        app.MapPost("/api/refresh",async(RefreshInput input,HttpContext context,Store store,SessionService sessions,WorkflowService jobs,ISqlGateway gateway,CancellationToken token)=>
        {
            var user=sessions.Current(context)!;var parsed=Authorize(input.Database,user,app.Configuration);
            if(!input.ConfirmReplace)throw new InputException("Подтвердите замену текущего содержимого базы.");
            if(input.Pii)throw new InputException("Обработка персональных данных ещё не включена. В этом контуре проверяется только сокращение.");
            var settings=store.GetSettings()??throw new InputException("Не настроено подключение.");
            var backupType=ResolveBackupType(store,parsed.SourceKey);
            if(input.Shrink)
            {
                if (!ShrinkBackups.Configured(settings))
                    throw new InputException("Каталог Shrink-копий ещё не настроен.");
                if(input.SourceDatabase is not null || input.BackupName is not null || input.Rules is {Length:>0})
                    throw new InputException("Готовая Shrink-копия используется как отдельный источник с сохранённым профилем.");
                if (ShrinkBackups.Available(store,settings,parsed.SourceKey) is null && input.ScheduledAt is null)
                    throw new InputException("Для этого типа пока нет готовой Shrink-копии.");
                var prepared=store.CreateJob("refresh",user.Name,parsed.SourceKey,input.Database,
                    new(settings,"",null,[],true,input.ConfirmDisconnect,UsePreparedShrink:true,ResolveLatestBackup:true),input.ScheduledAt);
                jobs.Enqueue(prepared);
                return Results.Accepted("/api/jobs/"+prepared.Id,prepared.Public());
            }
            if(input.Rules is {Length:>0})
                throw new InputException("Выберите один источник актуализации.");
            string? sourceDatabase=null;
            if(input.SourceDatabase is not null)
            {
                if(input.BackupName is not null || !WorkflowRules.IsCopySource(input.SourceDatabase,input.Database,parsed.SourceKey))
                    throw new InputException("Выберите один источник. Подходит резервный файл или другая Dev-база того же типа.");
                var databases=await gateway.List(settings,token);
                sourceDatabase=CopySources(databases,input.Database,parsed.SourceKey)
                    .FirstOrDefault(x=>x.Name.Equals(input.SourceDatabase,StringComparison.OrdinalIgnoreCase))?.Name
                    ??throw new InputException("База-источник не найдена или сейчас недоступна.");
            }
            if (sourceDatabase is null && input.ScheduledAt is null)
                BackupCatalog.RequireLatest(settings.BackupDirectory,backupType);
            var job=store.CreateJob("refresh",user.Name,parsed.SourceKey,input.Database,
                new(settings,"",null,[],false,input.ConfirmDisconnect,sourceDatabase,ResolveLatestBackup:sourceDatabase is null),input.ScheduledAt);
            jobs.Enqueue(job);
            return Results.Accepted("/api/jobs/"+job.Id,job.Public());
        });
        app.MapGet("/api/jobs",(HttpContext context,Store store,SessionService sessions)=>
            Results.Ok(store.GetJobs().Where(j=>WorkflowRules.CanRead(j,sessions.Current(context)!)).Select(j=>j.Public())));
        app.MapGet("/api/jobs/dashboard",(HttpContext context,Store store,SessionService sessions)=>
        {
            var now=DateTimeOffset.UtcNow;var user=sessions.Current(context)!;
            var visible = store.GetDashboardJobs(now).Where(j=>WorkflowRules.CanRead(j,user)).ToArray();
            var durations = store.GetDashboardDurations(visible,user);
            return Results.Ok(new {ServerTime=now,CompletedVisibleSeconds=300,
                Jobs=visible.Select(j=>new
                {j.Id,j.Kind,j.Actor,j.Target,j.SourceKey,j.Status,j.Stage,j.CreatedAt,j.UpdatedAt,j.ScheduledAt,j.StartedAt,j.Error,
                    j.ReadyAt,j.BackgroundStatus,j.BackgroundError,
                    Shrink=j.Kind=="refresh"&&j.Request.Shrink,
                    EstimatedDurationMs=durations.TryGetValue(j.Id,out var duration)?duration:(long?)null})});
        });
        app.MapGet("/api/jobs/{id}",(string id,HttpContext context,Store store,SessionService sessions)=>
        {
            var job=store.GetJob(id);
            if(job is null || !WorkflowRules.CanRead(job,sessions.Current(context)!)) return Results.NotFound();
            var report = job.Status is "Succeeded" or "Failed" or "Interrupted" ? store.GetExecutionReport(id) : null;
            return Results.Ok(job.Public(report));
        });
    }

    private static (SqlSettings Settings, ReductionProfile Profile) RequireShrinkBuildSettings(Store store,string source)
    {
        var settings=store.GetSettings();
        if (settings is null || !ShrinkBackups.Configured(settings))
            throw new InputException("Укажите папку Shrink-копий в настройках.");
        var profile=store.GetProfile(source);
        if (profile is null || !WorkflowRules.ProfileMatchesBackup(profile,ResolveBackupType(store,source)))
            throw new InputException("Сохраните профиль сокращения для текущего источника этого типа базы.");
        return (settings,profile);
    }

    private static string ResolveBackupType(Store store,string targetType) =>
        store.GetBackupAliases().GetValueOrDefault(targetType,targetType);

    private static IEnumerable<SqlDatabaseInfo> CopySources(IEnumerable<SqlDatabaseInfo> databases,string target,string sourceKey) =>
        databases.Where(x=>x.State=="ONLINE" && !x.IsSnapshot && WorkflowRules.IsCopySource(x.Name,target,sourceKey));

    private static DatabaseName Authorize(string database,AppUser user,IConfiguration configuration)
    {
        var parsed=Rules.ParseDatabase(database);
        if(parsed is null||!user.IsAdmin&&parsed.OwnerKey!=user.OwnerKey)throw new InputException("База недоступна для вашей учётной записи.");
        var allowed=configuration.GetSection("TestContour:AllowedTargets").Get<string[]>();
        if(allowed is {Length:>0}&&!allowed.Contains(database,StringComparer.OrdinalIgnoreCase))throw new InputException("В тестовом контуре эта база не разрешена как назначение.");
        return parsed;
    }
}
