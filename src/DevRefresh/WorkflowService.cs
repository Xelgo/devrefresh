using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace DevRefresh;

public sealed class WorkflowService(Store store,RestoreSql sql,StructureAnalyzer analyzer,ReductionExecutor executor,
    IWebHostEnvironment environment,IConfiguration configuration,ILogger<WorkflowService> logger):BackgroundService
{
    private readonly Channel<bool> queueSignal=Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {SingleReader=true,FullMode=BoundedChannelFullMode.DropWrite});
    private readonly string dataPath=Path.GetFullPath(configuration["DataDirectory"]??"App_Data",environment.ContentRootPath);
    private readonly Dictionary<string,AnalysisData> analysisCache=new(StringComparer.Ordinal);
    private readonly object shrinkFiles=new();
    private readonly object dispatchGate=new();
    private bool pausedForUpdate;
    private const string PrepareBackgroundMaintenance="Подготовка фонового освобождения места";
    private sealed record RunResult(object Value,WorkflowRequest? MaintenanceRequest=null,string? CompletionStage=null);
    private const string ShrinkAlreadyCurrent = "Сборка пропущена: доступная Shrink-копия уже актуальна";
    public void Enqueue(WorkflowJob job)
    {
        if (job.Status=="Queued") SignalQueue();
    }
    private void SignalQueue() => queueSignal.Writer.TryWrite(true);
    internal bool PauseForUpdate()
    {
        lock (dispatchGate)
        {
            if (pausedForUpdate || store.HasExecutingJobs()) return false;
            pausedForUpdate=true;
            return true;
        }
    }
    internal void ResumeAfterUpdate()
    {
        lock (dispatchGate) pausedForUpdate=false;
        SignalQueue();
    }
    public AnalysisData ReadAnalysis(string id)
    {
        if(!Guid.TryParseExact(id,"N",out _))throw new InputException("Некорректный идентификатор анализа.");
        lock(analysisCache)if(analysisCache.TryGetValue(id,out var cached))return cached;
        var path=Path.Combine(dataPath,"analyses",id+".json");
        if(!File.Exists(path))throw new InputException("Завершённый анализ не найден.");
        var value=JsonSerializer.Deserialize<AnalysisData>(File.ReadAllText(path))!;
        var versions=Array.FindIndex(value.Tables,table=>table.Key==WorkflowRules.ObjectVersionsRegister &&
            table.Reason=="Обязательная полная очистка при каждой актуализации.");
        if (versions>=0 && value.Tables[versions].Keys.Length>0)
            value.Tables[versions]=value.Tables[versions] with {CanSelect=true,Reason=null,DateColumn=null};
        lock(analysisCache){if(analysisCache.Count>=2)analysisCache.Remove(analysisCache.Keys.First());analysisCache[id]=value;}
        return value;
    }
    private void SaveAnalysis(AnalysisData value)
    {
        var folder=Path.Combine(dataPath,"analyses");Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,value.Id+".json"),JsonSerializer.Serialize(value));
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!configuration.GetValue("Workflows:Execute",true)){await Task.Delay(Timeout.Infinite,stoppingToken);return;}
        var maxParallelJobs = Math.Max(1,configuration.GetValue("Workflows:MaxParallelJobs",2));
        var running = new Dictionary<string,Task>(StringComparer.OrdinalIgnoreCase);
        Task? maintenance = null;
        store.InterruptRunningJobs();
        await CleanupCompleted(stoppingToken);
        using var backgroundStop=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var token = backgroundStop.Token;
        var cleanup=CleanupLoop(token);
        var schedule=ScheduleLoop(token);
        SignalQueue();
        try
        {
            await foreach (var signal in queueSignal.Reader.ReadAllAsync(token))
            {
                token.ThrowIfCancellationRequested();
                foreach (var entry in running.Where(entry=>entry.Value.IsCompleted).ToArray())
                {
                    running.Remove(entry.Key);
                    await entry.Value;
                }
                if (maintenance is {IsCompleted:true})
                {
                    await maintenance;
                    maintenance=null;
                }
                lock (dispatchGate)
                {
                    if (pausedForUpdate || running.Count == maxParallelJobs) continue;
                    foreach (var job in store.GetQueued())
                    {
                        token.ThrowIfCancellationRequested();
                        var lane = job.Kind=="shrink-build" ? "shrink-build:"+job.SourceKey : job.SourceKey;
                        if (running.ContainsKey(lane) || !store.TryStartJob(job.Id,DateTimeOffset.UtcNow)) continue;
                        var timing = new WorkflowRunTiming(job.Request.SourceDatabase is not null);
                        var task = Task.Run(()=>ExecuteJob(job,timing,token));
                        running.Add(lane,task);
                        _ = task.ContinueWith(_=>SignalQueue(),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                        if (running.Count == maxParallelJobs) break;
                    }
                    if (running.Count==0 && maintenance is null && store.GetNextBackgroundMaintenance() is { } background)
                    {
                        store.StartBackgroundMaintenance(background.Id,PrepareBackgroundMaintenance);
                        maintenance=Task.Run(()=>RunBackgroundMaintenance(background,token));
                        _ = maintenance.ContinueWith(_=>SignalQueue(),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                    }
                }
            }
        }
        finally
        {
            backgroundStop.Cancel();
            try{await Task.WhenAll(running.Values.Append(cleanup).Append(schedule).Append(maintenance??Task.CompletedTask));}catch(OperationCanceledException){}
        }
    }
    private async Task ExecuteJob(WorkflowJob job,WorkflowRunTiming timing,CancellationToken token)
    {
        var id = job.Id;
        try
        {
            var result=await Run(job,timing,token);
            if (result.MaintenanceRequest is { } request)
            {
                store.QueueBackgroundMaintenance(id,request,timing.Finish());
                return;
            }
            var complete = result.CompletionStage ?? (job.Kind switch
            {
                "refresh" => "База восстановлена",
                "preview" => "План проверен",
                "shrink-build" => "Shrink-копия опубликована",
                _ => "Анализ завершён"
            });
            store.UpdateJob(id,"Succeeded",complete,result.Value,
                executionReport:timing.Finish());
            store.Audit(job.Actor,job.Kind+".completed",$"Задание {id}, база {job.Target??"анализ"}: завершено.");
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested)
        {
            store.UpdateJob(id,"Interrupted","Остановлено с приложением",error:"Операция прервана. Автоматическое восстановление предыдущей базы не выполняется.",
                executionReport:timing.Finish());
        }
        catch(Exception ex)
        {
            var message=ex switch
            {
                InputException=>ex.Message,
                SqlException sqlError=>$"SQL {sqlError.Number}: {sqlError.Message}",
                SqlServiceException { Number: int number }=>$"SQL {number}: {ex.Message}",
                _=>"Не удалось выполнить задание. "+ex.Message
            };
            if(message.Length>1800)message=message[..1800];
            var failedStage=store.GetJob(id)?.Stage??"Ошибка";
            logger.LogError(ex,"Workflow {JobId} failed at {Stage}",id,failedStage);
            store.UpdateJob(id,"Failed",failedStage,error:message,executionReport:timing.Finish());
            store.Audit(job.Actor,job.Kind+".failed",$"Задание {id}: {message}");
        }
    }
    private async Task ScheduleLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            if (token.IsCancellationRequested) return;
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (store.QueueDueJobs(now).Length>0) SignalQueue();
                QueueShrinkBuilds(now);
            }
            catch (SqliteException error)
            {
                logger.LogWarning(error,"Не удалось проверить запланированные задания");
            }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private void QueueShrinkBuilds(DateTimeOffset now)
    {
        var settings = store.GetSettings();
        if (settings is null || !ShrinkBackups.Configured(settings)) return;
        var aliases = store.GetBackupAliases();
        BackupFile[]? backups = null;
        foreach (var (schedule, lastSlot) in store.GetShrinkScheduleStates())
        {
            if (schedule.DueSlot(now, TimeZoneInfo.Local) is not { } slot) continue;
            var slotKey = slot.ToString("O");
            if (string.CompareOrdinal(slotKey, lastSlot) <= 0) continue;
            var profile = store.GetProfile(schedule.SourceKey);
            var source = aliases.GetValueOrDefault(schedule.SourceKey, schedule.SourceKey);
            if (profile is null || !WorkflowRules.ProfileMatchesBackup(profile, source)) continue;

            backups ??= BackupCatalog.Scan(settings.BackupDirectory).Files;
            var full = backups.FirstOrDefault(backup => backup.SourceKey == source);
            var ready = ShrinkBackups.Available(store, settings, schedule.SourceKey);
            if (full?.BackupDate is { } fullDate && ready is not null && fullDate <= ready.DataDate)
            {
                if (store.TrySkipShrinkBuild(schedule, slotKey))
                    logger.LogInformation("Skipped scheduled Shrink build for {SourceKey} at {Slot}. Full backup date {FullDate}, Shrink data date {DataDate}",
                        schedule.SourceKey, slot, fullDate, ready.DataDate);
                continue;
            }

            var job = store.TryQueueShrinkBuild(schedule,
                new(settings, "", profile, profile.Rules, true, false, BuildSlot:slotKey));
            if (job is not null) Enqueue(job);
        }
    }
    private async Task CleanupLoop(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        while(await timer.WaitForNextTickAsync(token))
        {
            await CleanupCompleted(token);
            CleanupShrinkBackups(token);
        }
    }

    private void CleanupShrinkBackups(CancellationToken token)
    {
        foreach (var item in store.GetShrinkBackupsToClean())
        {
            if (token.IsCancellationRequested) return;
            try
            {
                lock (shrinkFiles)
                {
                    if (!Directory.Exists(item.Directory)) continue;
                    File.Delete(item.PathInDirectory);
                    store.ForgetShrinkBackup(item.Backup.Id);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(error, "Не удалось убрать файл Shrink-копии {BackupId}", item.Backup.Id);
            }
        }
    }
    private async Task CleanupCompleted(CancellationToken token)
    {
        foreach(var job in store.GetCleanupJobs())
        {
            if(token.IsCancellationRequested)return;
            try
            {
                if (job.Request.SourceDatabase is not null && job.ResourceName==DatabaseCopy.BackupName(job.Id))
                {
                    DatabaseCopy.DeleteBackup(job);
                    store.ClearResource(job.Id);
                    continue;
                }
                if(job.Request.SourceDatabase is not null && job.Target is not null && job.PinnedBackup==DatabaseCopy.PreviousName(job.Id))
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await using var owner=await sql.Open(job.Request.Settings,token:timeout.Token);
                    if(!await DatabaseCopy.TryRefreshLock(owner,job.Target,"Exclusive",timeout.Token))continue;
                    await DatabaseCopy.FinishPublish(owner,job.Target,"DevRefreshStage_"+job.Id,job.PinnedBackup,timeout.Token);
                    await CleanupStage(job);
                    continue;
                }
                await CleanupStage(job);
                logger.LogInformation("Removed completed service database {Resource}",job.ResourceName);
            }
            catch(Exception ex){logger.LogWarning(ex,"Resource cleanup pending for job {JobId}",job.Id);}
        }
    }

    private async Task CleanupStage(WorkflowJob job)
    {
        var resource=job.ResourceName??"DevRefreshStage_"+job.Id;
        for(var attempt=0;;attempt++)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try{await sql.DropOwned(job.Request.Settings,resource,job.Id,timeout.Token,registered:true);break;}
            catch when(attempt<2){await Task.Delay(250);}
        }
        // Remove only exact files generated by old versions for this registered job.
        var folder=RestoreSql.WorkPath(job.Request.Settings.BackupDirectory,"");
        foreach(var suffix in new[]{"-source.bak","-source.bak.partial","-ready.bak","-previous.bak"})
        {
            var file=Path.Combine(folder,job.Id+suffix);
            try{if(File.Exists(file))File.Delete(file);}
            catch(Exception ex){logger.LogWarning(ex,"Could not remove obsolete working file for job {JobId}",job.Id);}
        }
        store.ClearResource(job.Id);
    }
    private async Task<RunResult> Run(WorkflowJob job,WorkflowRunTiming timing,CancellationToken token)
    {
        void LiveProgress(string text)=>store.UpdateJob(job.Id,"Running",text);
        void Progress(string text)
        {
            timing.ChangeStage(text);
            LiveProgress(text);
        }
        var settings=job.Request.Settings;
        if (job.Kind=="shrink-build")
            return await BuildShrinkBackup(job,timing,Progress,LiveProgress,token);
        if(job.Request.SourceDatabase is { } sourceDatabase)
            return await RunCopy(job,sourceDatabase,timing,Progress,token);
        ShrinkBackup? ready=null;
        BackupSource OpenSource()
        {
            if (!job.Request.ResolveLatestBackup)
                return sql.OpenBackupSource(settings,job.Request.BackupName,token);
            var current=store.GetSettings() ?? throw new InputException("Не настроено подключение.");
            if (!string.Equals(current.Server,settings.Server,StringComparison.OrdinalIgnoreCase))
                throw new InputException("SQL Server изменился после постановки задания. Запустите актуализацию заново.");
            settings=current;
            if (!job.Request.UsePreparedShrink)
            {
                var type=store.GetBackupAliases().GetValueOrDefault(job.SourceKey,job.SourceKey);
                var full=BackupCatalog.RequireLatest(settings.BackupDirectory,type);
                job=job with {Request=job.Request with {Settings=settings,BackupName=full.Name}};
                return sql.OpenBackupSource(settings,full.Name,token);
            }
            lock (shrinkFiles)
            {
                ready=ShrinkBackups.Available(store,settings,job.SourceKey)
                    ??throw new InputException("Для этого типа пока нет готовой Shrink-копии.");
                job=job with {Request=job.Request with {Settings=settings,BackupName=ready.Name}};
                var preparedSettings=settings with
                {
                    BackupDirectory=settings.ShrinkBackupDirectory, SqlBackupDirectory=settings.SqlShrinkBackupDirectory
                };
                return sql.OpenBackupSource(preparedSettings,ready.Name,token);
            }
        }
        using var source=OpenSource();
        if (job.Request.ResolveLatestBackup)
            logger.LogInformation("Workflow {JobId} selected backup {BackupName} for {SourceKey}",
                job.Id,job.Request.BackupName,job.SourceKey);
        if(job.Kind is "analysis" or "preview")
        {
            var stage="DevRefreshStage_"+job.Id;
            timing.ChangeStage("Подготовка временной базы");
            store.UpdateJob(job.Id,"Running","Подготовка временной базы",resource:stage);
            Exception? analysisFailure=null;
            try
            {
                await sql.Restore(settings,stage,source.SqlPath,job.Id,false,token,progress:Progress);
                Progress("Чтение метаданных 1С и размеров таблиц");
                var analysis=await analyzer.Analyze(settings,stage,job.Id,job.SourceKey,job.Request.BackupName,token,includePeriodStatistics:job.Kind=="analysis");
                if(job.Kind=="preview")
                {
                    ValidateProfile(job,analysis);
                    return new(await executor.Apply(settings,stage,analysis,job.Request.Rules,true,Progress,token,previewOnly:true,
                        liveProgress:LiveProgress,measureTable:timing.MeasureTable,recordStage:timing.ChangeStage,jobId:job.Id));
                }
                Progress("Сохранение анализа");
                SaveAnalysis(analysis);
                return new(new {AnalysisId=job.Id,analysis.Configuration,analysis.ConfigurationVersion,TableCount=analysis.Tables.Length,
                    UsedMB=analysis.Tables.Sum(t=>t.UsedMB),Warnings=analysis.Warnings,TemporaryDatabaseDeleted=true});
            }
            catch(Exception ex){analysisFailure=ex;throw;}
            finally
            {
                // A fresh token ensures cancellation/shutdown does not skip DROP DATABASE.
                try
                {
                    const string cleanup = "Удаление временной базы анализа";
                    if(analysisFailure is null)Progress(cleanup); else timing.ChangeStage(cleanup);
                    await CleanupStage(store.GetJob(job.Id)!);
                }
                catch(Exception ex)
                {
                    logger.LogError(ex,"Service database cleanup pending for job {JobId}",job.Id);
                    if(analysisFailure is null)throw;
                }
            }
        }
        return await RestoreDeveloperDatabase(job,source.SqlPath,settings,ready,timing,Progress,token);
    }

    private async Task<RunResult> RestoreDeveloperDatabase(WorkflowJob job,string backupPath,SqlSettings settings,
        ShrinkBackup? ready,WorkflowRunTiming timing,Action<string> progress,CancellationToken token)
    {
        if(job.Kind!="refresh" || job.Target is not { } target || Rules.ParseDatabase(target)?.SourceKey!=job.SourceKey)
            throw new InputException("Некорректная база назначения.");
        progress("Проверка базы назначения и соединений");
        await using var owner=await sql.Open(settings,token:token);
        if(!await DatabaseCopy.TryRefreshLock(owner,target,"Exclusive",token))
            throw new InputException("База уже обновляется или используется как источник копирования.");
        if(await RestoreSql.Scalar<int>(owner,$"SELECT COUNT(*) FROM sys.databases WHERE name={RestoreSql.L(target)} AND database_id>4 AND source_database_id IS NULL",token)!=1)
            throw new InputException("База назначения не существует.");
        if(!job.Request.ConfirmDisconnect && await sql.Sessions(settings,target,token)>0)
            throw new InputException("В базе есть соединения. Подтвердите их завершение.");
        var touched=false;
        var opened=false;
        Exception? failure=null;
        try
        {
            var backupFileSize=await sql.Restore(settings,target,backupPath,job.Id,true,token,owner,
                job.Request.ConfirmDisconnect,progress,replacing:()=>touched=true);
            timing.FileSizes.Add(backupFileSize);
            await using (var restored=await sql.Open(settings,target,token))
            {
                await CaptureDeveloperSize(restored,target,"После восстановления",timing,progress,token);
                await CheckDeveloperCopy(restored,target,settings,progress,token);
            }
            progress("Возврат базы в многопользовательский режим");
            await OpenDeveloperAccess(owner,target,job.Request.ConfirmDisconnect,token);
            opened=true;
            var result=new {Database=target,Backup=job.Request.BackupName,job.Request.SourceDatabase,Shrink=job.Request.UsePreparedShrink,
                ShrinkDataDate=ready?.DataDate,PersonalDataProcessed=false,
                RestoreMode=job.Request.SourceDatabase is not null?"copy":job.Request.UsePreparedShrink?"prepared-shrink":"direct",
                SqlCheck=settings.RunDbccCheckDb?"passed":"skipped",TotalRows=0};
            store.MarkDatabaseReady(job.Id,result);
            return new(result,job.Request.UsePreparedShrink?null:job.Request);
        }
        catch(Exception ex){failure=ex;throw;}
        finally
        {
            if(touched && !opened)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    const string cleanup = "Возврат базы в многопользовательский режим";
                    if(failure is null)progress(cleanup); else timing.ChangeStage(cleanup);
                    await OpenDeveloperAccess(owner,target,job.Request.ConfirmDisconnect,timeout.Token);
                }
                catch(Exception ex)
                {
                    logger.LogError(ex,"Cannot return {Database} to MULTI_USER after job {JobId}",job.Target,job.Id);
                    if(failure is null)throw;
                }
            }
        }
    }

    private static Task OpenDeveloperAccess(SqlConnection owner,string database,bool disconnect,CancellationToken token) =>
        RestoreSql.Execute(owner,$"ALTER DATABASE {RestoreSql.Q(database)} SET MULTI_USER"+
            (disconnect?" WITH ROLLBACK IMMEDIATE":" WITH NO_WAIT"),token);

    private static async Task CheckDeveloperCopy(SqlConnection connection,string database,SqlSettings settings,
        Action<string> progress,CancellationToken token)
    {
        progress("Контрольная точка перед открытием базы");
        await RestoreSql.Execute(connection,"CHECKPOINT",token);
        if (!settings.RunDbccCheckDb) return;
        progress("Проверка целостности SQL-базы");
        await RestoreSql.Execute(connection,$"DBCC CHECKDB({RestoreSql.Q(database)}) WITH NO_INFOMSGS,ALL_ERRORMSGS",token);
    }

    private async Task CaptureDeveloperSize(SqlConnection connection,string database,string stage,
        WorkflowRunTiming timing,Action<string> progress,CancellationToken token)
    {
        progress($"Замер файлов SQL · {stage}");
        try { timing.FileSizes.Add(await RestoreSql.ReadFileSize(connection,stage,token)); }
        catch(SqlException error) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(error,"Не удалось измерить файлы базы {Database} на этапе {Stage}",database,stage);
        }
    }

    private async Task RunBackgroundMaintenance(WorkflowJob job,CancellationToken token)
    {
        var id=job.Id;
        var database=job.Target!;
        var settings=job.Request.Settings;
        var previous=store.GetExecutionReport(id)!;
        var timing=new WorkflowRunTiming(previous,PrepareBackgroundMaintenance);
        void LiveProgress(string text)=>store.UpdateJob(id,"Running",text);
        void Progress(string text)
        {
            timing.ChangeStage(text);
            LiveProgress(text);
        }
        var status="Succeeded";
        string? message=null;
        try
        {
            await using var owner=await sql.Open(settings,token:token);
            var version=await RestoreSql.Scalar<int>(owner,"SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion'))",token);
            if (version<16)
            {
                status="Skipped";
                message="Фоновое сжатие с низким приоритетом доступно с SQL Server 2022.";
            }
            else if (!await DatabaseCopy.TryRefreshLock(owner,database,"Shared",token))
            {
                status="Skipped";
                message="База уже обновляется. Фоновое освобождение места пропущено.";
            }
            else
            {
                await using var connection=await sql.Open(settings,database,token);
                timing.FileOptimization=await sql.OptimizeFiles(settings,connection,Progress,LiveProgress,token,
                    recordSize:timing.FileSizes.Add,logger:logger,jobId:id,lowPriority:true);
                await CaptureDeveloperSize(connection,database,"После освобождения места",timing,Progress,token);
            }
        }
        catch (Exception error)
        {
            var interrupted=token.IsCancellationRequested;
            status=interrupted?"Interrupted":"Failed";
            message=interrupted ? "Освобождение места остановлено вместе со службой." : error.Message;
            logger.LogWarning(error,"Background file optimization stopped for job {JobId} on {Database}",id,database);
            if (message.Length>1800) message=message[..1800];
        }
        store.FinishBackgroundMaintenance(id,status,message,timing.Finish());
        store.Audit(job.Actor,"refresh.completed",$"Задание {id}, база {database}: завершено.");
    }

    private async Task<RunResult> BuildShrinkBackup(WorkflowJob job, WorkflowRunTiming timing,
        Action<string> progress, Action<string> liveProgress, CancellationToken token)
    {
        var settings=job.Request.Settings;
        var profile=job.Request.Profile ?? throw new InputException("Не найден профиль сокращения.");
        var backupType=WorkflowRules.ParseBackupName(profile.BackupName)?.SourceKey
            ?? throw new InputException("Не определён тип исходного бэкапа.");
        var full=BackupCatalog.RequireLatest(settings.BackupDirectory,backupType);
        var current=ShrinkBackups.Available(store,settings,job.SourceKey);
        var fullDate=full.BackupDate;
        if (job.Request.BuildSlot is null && fullDate < current?.DataDate)
            throw new InputException("Самый свежий доступный полный бэкап старее опубликованной Shrink-копии.");
        if (!Directory.Exists(settings.ShrinkBackupDirectory))
            throw new InputException("Папка Shrink-копий недоступна службе DevRefresh. Проверьте путь и права.");
        var stage="DevRefreshStage_"+job.Id;
        DateTime? backupFinish=null;
        using var source=sql.OpenBackupSource(settings,full.Name,token);
        if (job.Request.BuildSlot is not null && current is not null)
        {
            if (fullDate is null)
            {
                progress("Чтение даты полного бэкапа");
                await using var connection=await sql.Open(settings,token:token);
                var header=await RestoreSql.ReadFullBackupHeader(connection,source.SqlPath,token);
                fullDate=header.GetValueOrDefault("BackupFinishDate") is DateTime finished
                    ? DateOnly.FromDateTime(finished) : DateOnly.FromDateTime(full.ModifiedAt.LocalDateTime);
            }
            if (fullDate <= current.DataDate)
                return new(new { Skipped=true, FullBackupName=full.Name, FullDataDate=fullDate, ShrinkDataDate=current.DataDate },
                    CompletionStage:ShrinkAlreadyCurrent);
        }
        store.UpdateJob(job.Id,"Running","Подготовка временной базы",resource:stage);
        StoredShrinkBackup published;
        Exception? failure=null;
        try
        {
            timing.FileSizes.Add(await sql.Restore(settings,stage,source.SqlPath,job.Id,false,token,
                progress:progress,backupFinished:date=>backupFinish=date));
            progress("Установка модели восстановления SIMPLE");
            await using var owner=await sql.Open(settings,token:token);
            await RestoreSql.Execute(owner,$"ALTER DATABASE {RestoreSql.Q(stage)} SET RECOVERY SIMPLE",token);
            var buildJob=job with {Request=job.Request with {BackupName=full.Name}};
            await PrepareShrinkFiles(buildJob,stage,timing,progress,liveProgress,token);
            await using (var staging=await sql.Open(settings,stage,token))
                await RestoreSql.Execute(staging,"EXEC sys.sp_dropextendedproperty @name=N'DevRefresh.Resource'",token);

            var dataDate=fullDate ?? DateOnly.FromDateTime(backupFinish ?? full.ModifiedAt.LocalDateTime);
            var item=new StoredShrinkBackup(new(job.Id,job.SourceKey,ShrinkBackups.FileName(job.SourceKey,job.Id),
                full.Name,dataDate,DateTimeOffset.UtcNow,0),settings.ShrinkBackupDirectory,settings.SqlShrinkBackupDirectory,settings.Server);
            if (File.Exists(item.PathInDirectory)) throw new InputException("Файл для новой Shrink-копии уже существует.");
            store.RegisterShrinkBackup(item);
            progress("Запись Shrink-бэкапа");
            await RestoreSql.Execute(owner,
                $"BACKUP DATABASE {RestoreSql.Q(stage)} TO DISK={RestoreSql.L(item.SqlPath)} WITH COPY_ONLY,COMPRESSION,CHECKSUM,INIT",
                token);
            progress("Проверка Shrink-бэкапа (RESTORE VERIFYONLY)");
            using var generated=sql.OpenBackupSource(settings with {BackupDirectory=item.Directory,SqlBackupDirectory=item.SqlDirectory},
                item.Backup.Name,token);
            await RestoreSql.Execute(owner,$"RESTORE VERIFYONLY FROM DISK={RestoreSql.L(generated.SqlPath)} WITH CHECKSUM",token);
            published=item with {Backup=item.Backup with {PreparedAt=DateTimeOffset.UtcNow,Size=new FileInfo(item.PathInDirectory).Length}};
        }
        catch (Exception error) { failure=error; throw; }
        finally
        {
            try
            {
                const string cleanup="Удаление временной базы Shrink";
                if (failure is null) progress(cleanup); else timing.ChangeStage(cleanup);
                await CleanupStage(store.GetJob(job.Id)!);
            }
            catch (Exception error) when (failure is not null)
            {
                logger.LogWarning(error,"Не удалось убрать временную базу Shrink задания {JobId}",job.Id);
            }
        }
        token.ThrowIfCancellationRequested();
        progress("Публикация Shrink-копии");
        lock (shrinkFiles) store.PublishShrinkBackup(published);
        return new(new {ShrinkBackup=published.Backup,timing.BinaryDataCleanup});
    }

    private async Task<RunResult> RunCopy(WorkflowJob job,string source,WorkflowRunTiming timing,
        Action<string> progress,CancellationToken token)
    {
        if(job.Kind!="refresh" || job.Target is not { } target || Rules.ParseDatabase(target)?.SourceKey!=job.SourceKey ||
            !WorkflowRules.IsCopySource(source,target,job.SourceKey) || !Guid.TryParseExact(job.Id,"N",out _))
            throw new InputException("Выберите другую Dev-базу того же типа.");

        var current=store.GetSettings() ?? throw new InputException("Не настроено подключение.");
        if (!string.Equals(current.Server,job.Request.Settings.Server,StringComparison.OrdinalIgnoreCase))
            throw new InputException("SQL Server изменился после постановки задания. Запустите актуализацию заново.");
        if (DatabaseCopy.BackupDirectories(current) is not { } paths || !Directory.Exists(paths.AppDirectory))
            throw new InputException("Укажите доступную папку временных .bak или настройте каталог Shrink-копий.");
        var filename=DatabaseCopy.BackupName(job.Id);
        var appPath=Path.Combine(paths.AppDirectory,filename);
        var sqlPath=Path.Combine(paths.SqlDirectory,filename);
        progress("Проверка базы-источника");

        try
        {
            await using (var connection=await sql.Open(current,token:token))
            {
                if (!await DatabaseCopy.TryRefreshLock(connection,source,"Shared",token))
                    throw new InputException("База-источник сейчас обновляется.");
                if (await RestoreSql.Scalar<int>(connection,$"""
                    SELECT COUNT(*) FROM sys.databases
                    WHERE name={RestoreSql.L(source)} AND database_id>4 AND state=0 AND source_database_id IS NULL
                    """,token)!=1)
                    throw new InputException("База-источник недоступна.");
                if (File.Exists(appPath))
                    throw new InputException("Временный .bak для этого задания уже существует.");

                const string backupStage="Создание временного .bak";
                store.RegisterCopyBackup(job.Id,filename,current,backupStage);
                job=job with {Request=job.Request with {Settings=current},ResourceName=filename};
                progress(backupStage);
                await DatabaseCopy.CreateBackup(connection,job,source,sqlPath,progress,logger,token);
            }

            // Check that DevRefresh can access the temporary file before changing the destination.
            using var backup=sql.OpenBackupSource(current with {BackupDirectory=paths.AppDirectory,SqlBackupDirectory=paths.SqlDirectory},
                filename,token);
            return await RestoreDeveloperDatabase(job,backup.SqlPath,current,null,timing,progress,token);
        }
        finally
        {
            if (job.ResourceName==filename)
            {
                try
                {
                    DatabaseCopy.DeleteBackup(job);
                    store.ClearResource(job.Id);
                }
                catch(Exception error)
                {
                    logger.LogWarning(error,"Temporary backup cleanup pending for job {JobId}",job.Id);
                }
            }
        }
    }

    private async Task PrepareShrinkFiles(WorkflowJob job,string database,WorkflowRunTiming timing,
        Action<string> progress,Action<string> liveProgress,CancellationToken token)
    {
        var settings=job.Request.Settings;
        async Task CaptureSize(string stage)
        {
            progress($"Замер файлов SQL · {stage}");
            try
            {
                await using var connection=await sql.Open(settings,database,token);
                timing.FileSizes.Add(await RestoreSql.ReadFileSize(connection,stage,token));
            }
            catch(SqlException error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning(error,"Не удалось измерить файлы базы {Database} на этапе {Stage}",database,stage);
            }
        }

        await CaptureSize("После восстановления");
        var usedBeforeDeletion=timing.FileSizes.LastOrDefault()?.DataUsedBytes;
        long? expectedUsedBytes=null;
        progress("Чтение структуры восстановленной базы");
        var analysis=await analyzer.Analyze(settings,database,job.Id,job.SourceKey,job.Request.BackupName,token);
        ValidateProfile(job,analysis);
        var reduction=await executor.Apply(settings,database,analysis,job.Request.Rules,job.Request.Shrink,progress,token,
            liveProgress:liveProgress,measureTable:timing.MeasureTable,recordStage:timing.ChangeStage,jobId:job.Id,
            cleanBinaryData:true);
        timing.BinaryDataCleanup=reduction.BinaryDataCleanup;
        var wholeTables=job.Request.Rules.Where(rule=>rule.Before is null).Select(rule=>rule.Key).ToHashSet(StringComparer.Ordinal);
        var expectedRelease=analysis.Tables.Where(table=>wholeTables.Contains(table.Key) && table.Rows>0).Sum(table=>table.UsedMB)
            +(reduction.BinaryDataCleanup?.PayloadBytes??0)/(1024m*1024m);
        if (expectedRelease>=256 && usedBeforeDeletion.HasValue)
            expectedUsedBytes=Math.Max(0,usedBeforeDeletion.Value-(long)(expectedRelease*0.85m*1024*1024));
        await CaptureSize("Перед освобождением места");
        progress("Подготовка освобождения места");
        await using var connection=await sql.Open(settings,database,token);
        timing.FileOptimization=await sql.OptimizeFiles(settings,connection,progress,liveProgress,token,
            deletedRows:reduction.TotalRows>0 || reduction.BinaryDataCleanup is {DeletedBlocks:>0},
            expectedUsedBytes:expectedUsedBytes,recordSize:timing.FileSizes.Add,
            logger:logger,jobId:job.Id);
        await CaptureSize("После освобождения места");
        if(settings.RunDbccCheckDb)
        {
            progress("Проверка восстановленной SQL-базы");
            await RestoreSql.Execute(connection,$"DBCC CHECKDB({RestoreSql.Q(database)}) WITH NO_INFOMSGS,ALL_ERRORMSGS",token);
        }
    }

    private void ValidateProfile(WorkflowJob job,AnalysisData analysis)
    {
        if(!job.Request.Shrink)return;
        var profile=job.Request.Profile??throw new InputException("Не найден профиль сокращения.");
        var original=ReadAnalysis(profile.AnalysisId);
        if(original.Configuration!=analysis.Configuration || original.ConfigurationVersion!=analysis.ConfigurationVersion || original.SchemaSignature!=analysis.SchemaSignature)
            throw new InputException("Структура бэкапа изменилась. Обновите профиль сокращения. "+
                (job.Kind=="refresh"&&job.Request.SourceDatabase is null?"База уже восстановлена; сокращение не выполнено.":"База разработчика не изменена."));
        WorkflowRules.ValidateSelection(analysis,job.Request.Rules,profile.Rules.Select(r=>r.Key).ToHashSet(StringComparer.Ordinal));
    }
}
