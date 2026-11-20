using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace DevRefresh;

internal sealed record OneCWorkerProcess(int Id,DateTime StartedAtUtc);
public sealed class OneCRecoveryException(string message):Exception(message);

public sealed class OneCDomainService(SettingsService settingsService,IDataProtectionProvider protection,IbcmdProcessRunner runner,
    IWebHostEnvironment environment,IConfiguration configuration,ILogger<OneCDomainService> logger)
{
    private readonly string folder=Path.Combine(Path.GetFullPath(configuration["DataDirectory"]??"App_Data",environment.ContentRootPath),"oneC-auth");
    private IDataProtector Protector=>protection.CreateProtector("DevRefresh.OneC.AuthRecovery.v1");

    public async Task<RestoreOperationResult[]> Apply(SqlSettings sql,RestorePipeline pipeline,string target,string id,Action<string> progress,CancellationToken token)
    {
        var settings=OneCConfiguration.Require(sql);
        var domains=pipeline.Operations.Select(operation=>operation.TargetDomain).ToArray();
        var request=new OneCWorkerRequest(id,target,settingsService.ConnectionString(sql),settings,domains,Path.Combine(folder,id+".epf"));
        Validate(request);
        Directory.CreateDirectory(folder);
        var journal=Path.Combine(folder,id+".protected");
        if(File.Exists(journal))throw new InputException("Для задания 1С уже существует незавершённое восстановление пользователей.");
        var input=JsonSerializer.Serialize(request);
        await File.WriteAllTextAsync(journal,Protector.Protect(input),token);
        try
        {
            await using(var source=typeof(OneCDomainService).Assembly.GetManifestResourceStream("DevRefresh.OneC.DevRefreshDomain.epf")
                ??throw new InputException("В поставке отсутствует обработка подмены домена."))
            await using(var destination=File.Create(request.ProcessorPath))await source.CopyToAsync(destination,token);
            progress("Операции после восстановления · Подмена домена через 1С");
            var password=new SqlConnectionStringBuilder(request.ConnectionString).Password;
            var result=await runner.Run(WorkerStart(id),input,TimeSpan.FromSeconds(settings.TimeoutSeconds),[password],token,"Операция 1С");
            if(result.ExitCode!=0)throw new InputException("Не удалось выполнить обработку 1С. "+result.Output);
            var written=JsonSerializer.Deserialize<OneCWorkerResult>(result.Output)??throw new InputException("Не получен результат обработки 1С.");
            DeleteJournal(request);
            return domains.Select((domain,index)=>new RestoreOperationResult(RestorePipelineRules.WindowsDomain,written.Written[index],0,domain)).ToArray();
        }
        catch
        {
            await Recover(request);
            throw;
        }
    }

    public async Task RecoverPending()
    {
        if(!Directory.Exists(folder))return;
        foreach(var path in Directory.EnumerateFiles(folder,"*.protected"))
        {
            var id=Path.GetFileNameWithoutExtension(path);
            if(!Guid.TryParseExact(id,"N",out _))continue;
            var request=JsonSerializer.Deserialize<OneCWorkerRequest>(Protector.Unprotect(await File.ReadAllTextAsync(path)))
                ??throw new InputException("Не удалось прочитать журнал восстановления пользователей 1С.");
            Validate(request);
            if(request.JobId!=id)throw new InputException("Журнал восстановления 1С относится к другому заданию.");
            await Recover(request);
        }
    }

    private async Task Recover(OneCWorkerRequest request)
    {
        Validate(request);
        var builder=new SqlConnectionStringBuilder(request.ConnectionString){InitialCatalog="master",Pooling=false};
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            StopWorker(request);
            await using var master=new SqlConnection(builder.ConnectionString);await master.OpenAsync(timeout.Token);
            if(await RestoreSql.Scalar<int>(master,"SELECT COUNT(*) FROM sys.databases WHERE name="+RestoreSql.L(request.Database)+" AND state=6",timeout.Token)==1)
                await RestoreSql.Execute(master,$"ALTER DATABASE {RestoreSql.Q(request.Database)} SET ONLINE",timeout.Token);
            builder.InitialCatalog=request.Database;
            await using var db=new SqlConnection(builder.ConnectionString);await db.OpenAsync(timeout.Token);
            await OneCUserSnapshot.Restore(db,request.JobId,timeout.Token);
            await OneCUserSnapshot.Remove(db,request.JobId,timeout.Token);
            DeleteJournal(request);
        }
        catch(Exception error)
        {
            logger.LogError("Не удалось восстановить список пользователей 1С для задания {JobId}; журнал сохранён",request.JobId);
            try
            {
                using var offlineTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                builder.InitialCatalog="master";
                await using var master=new SqlConnection(builder.ConnectionString);await master.OpenAsync(offlineTimeout.Token);
                await RestoreSql.Execute(master,$"ALTER DATABASE {RestoreSql.Q(request.Database)} SET OFFLINE WITH ROLLBACK IMMEDIATE",offlineTimeout.Token);
            }
            catch(Exception offlineError){logger.LogError("Не удалось закрыть SQL-базу после ошибки восстановления пользователей: {ErrorType}",offlineError.GetType().Name);}
            throw new OneCRecoveryException("Не удалось вернуть список пользователей 1С. База оставлена закрытой; журнал восстановления сохранён. Причина: "+error.GetType().Name);
        }
    }

    private void Validate(OneCWorkerRequest request)
    {
        if(!Guid.TryParseExact(request.JobId,"N",out _) || Rules.ParseDatabase(request.Database) is null
            || Path.GetFullPath(request.ProcessorPath)!=Path.Combine(folder,request.JobId+".epf"))
            throw new InputException("Некорректный журнал операции 1С.");
    }
    private void DeleteJournal(OneCWorkerRequest request)
    {
        foreach(var extension in new[]{"protected","epf","pid"})
            try{File.Delete(Path.Combine(folder,request.JobId+"."+extension));}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException)
            {logger.LogWarning("Не удалось удалить временный файл операции 1С {JobId}.{Extension}",request.JobId,extension);}
    }
    private static ProcessStartInfo WorkerStart(string id)
    {
        var executable=Environment.ProcessPath??throw new InputException("Не найден исполняемый файл DevRefresh.");
        var start=new ProcessStartInfo(executable);
        if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"DevRefresh.dll"));
        foreach(var value in new[]{"--Maintenance","oneC-domain","--OneCJobId",id})start.ArgumentList.Add(value);
        return start;
    }
    private void StopWorker(OneCWorkerRequest request)
    {
        var path=Path.ChangeExtension(request.ProcessorPath,"pid");
        if(!File.Exists(path))return;
        var saved=JsonSerializer.Deserialize<OneCWorkerProcess>(File.ReadAllText(path));
        if(saved is null || saved.Id==Environment.ProcessId)return;
        try
        {
            using var process=Process.GetProcessById(saved.Id);
            if(process.StartTime.ToUniversalTime()!=saved.StartedAtUtc
                || !string.Equals(process.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))return;
            process.Kill(entireProcessTree:true);process.WaitForExit(10000);
        }
        catch(ArgumentException){}
        catch(InvalidOperationException){}
    }
}
