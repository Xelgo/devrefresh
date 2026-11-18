namespace DevRefresh;

public sealed partial class WorkflowService
{
    private async Task<RunResult> RunDt(WorkflowJob job,WorkflowRunTiming timing,Action<string> progress,CancellationToken token)
    {
        if(job.Kind!="refresh" || job.Target is not {} target || Rules.ParseDatabase(target)?.SourceKey!=job.SourceKey ||
            job.Request.Shrink || job.Request.SourceDatabase is not null || !Guid.TryParseExact(job.Id,"N",out _))
            throw new InputException("Некорректное задание загрузки .dt.");
        var settings=store.GetSettings()??throw new InputException("Не настроено подключение.");
        if(!string.Equals(settings.Server,job.Request.Settings.Server,StringComparison.OrdinalIgnoreCase))
            throw new InputException("SQL Server изменился после постановки задания. Запустите актуализацию заново.");
        progress("Подбор архива .dt");
        var type=store.GetBackupAliases().GetValueOrDefault(job.SourceKey,job.SourceKey);
        var backup=DtRestoreConfiguration.RequireLatest(settings,type);
        var dt=settings.DtRestore!;
        var archive=Path.Combine(dt.BackupDirectory,backup.Name);
        var info=new FileInfo(archive);
        if(!info.Exists || info.Length==0 || (info.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new InputException("Архив .dt недоступен, пуст или является ссылкой.");
        using var source=File.Open(archive,FileMode.Open,FileAccess.Read,FileShare.Read);
        progress("Проверка IBCMD и параметров загрузки");
        var version=await ibcmd.Probe(settings,token);
        var root=dt.WorkingDirectory.Length>0?dt.WorkingDirectory:Path.Combine(dataPath,"ibcmd");
        var work=Path.Combine(root,"dt-"+job.Id);
        if(Directory.Exists(work))throw new InputException("Рабочий каталог этого задания IBCMD уже существует. Создайте новое задание.");
        var launch=ibcmd.Prepare(settings,target,archive,work,job.Request.ConfirmDisconnect,version);
        Directory.CreateDirectory(work);
        progress("Проверка базы назначения и соединений");
        await using var owner=await sql.Open(settings,token:token);
        if(!await DatabaseCopy.TryRefreshLock(owner,target,"Exclusive",token))
            throw new InputException("База уже обновляется или используется как источник копирования.");
        if(await RestoreSql.Scalar<int>(owner,$"SELECT COUNT(*) FROM sys.databases WHERE name={RestoreSql.L(target)} AND database_id>4 AND source_database_id IS NULL AND state=0",token)!=1)
            throw new InputException("База назначения не существует, недоступна или является снимком.");
        if(!job.Request.ConfirmDisconnect && await sql.Sessions(settings,target,token)>0)
            throw new InputException("В базе есть соединения. Подтвердите их завершение.");
        var touched=false;var opened=false;
        RestoreOperationResult[] operations=[];
        try
        {
            progress("Загрузка .dt через IBCMD");
            touched=true;
            await ibcmd.Execute(launch,token);
            progress("Возврат базы в многопользовательский режим");
            await OpenDeveloperAccess(owner,target,job.Request.ConfirmDisconnect,token);
            opened=true;
            await using(var restored=await sql.Open(settings,target,token))
            {
                await CaptureDeveloperSize(restored,target,"После загрузки .dt",timing,progress,token);
                operations=await pipelineExecutor.Apply(restored,job.Request.Pipeline,progress,token);
                await CheckDeveloperCopy(restored,target,settings,progress,token);
            }
            var result=new {Database=target,Backup=backup.Name,RestoreMode="dt",DtDataDate=backup.BackupDate,
                IbcmdVersion=version.ToString(),Shrink=false,PersonalDataProcessed=false,
                SqlCheck=settings.RunDbccCheckDb?"passed":"skipped",TotalRows=0,PostRestoreOperations=operations};
            store.MarkDatabaseReady(job.Id,result);
            return new(result);
        }
        finally
        {
            if(touched && !opened)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await OpenDeveloperAccess(owner,target,job.Request.ConfirmDisconnect,timeout.Token); }
                catch(Exception error){logger.LogError(error,"Cannot return {Database} to MULTI_USER after DT job {JobId}",target,job.Id);}
            }
        }
    }
}
