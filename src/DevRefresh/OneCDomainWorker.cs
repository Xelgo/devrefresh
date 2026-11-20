using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace DevRefresh;

internal sealed record OneCWorkerRequest(string JobId,string Database,string ConnectionString,OneCSettings Settings,string[] Domains,string ProcessorPath);
internal sealed record OneCWorkerResult(int[] Written);

internal static class OneCDomainWorker
{
    public static int Run(string json)
    {
        if(!OperatingSystem.IsWindows())throw new InputException("Операция 1С доступна только в Windows.");
        var request=JsonSerializer.Deserialize<OneCWorkerRequest>(json)??throw new InputException("Нет параметров операции 1С.");
        if(!Guid.TryParseExact(request.JobId,"N",out _) || Rules.ParseDatabase(request.Database) is null)
            throw new InputException("Некорректная база или задание операций 1С.");
        OneCConfiguration.Validate(request.Settings);
        foreach(var domain in request.Domains)RestorePipelineRules.ValidateDomain(domain);
        using(var process=Process.GetCurrentProcess())
            File.WriteAllText(Path.ChangeExtension(request.ProcessorPath,"pid"),JsonSerializer.Serialize(new OneCWorkerProcess(process.Id,process.StartTime.ToUniversalTime())));
        Exception? failure=null;OneCWorkerResult? result=null;
        var thread=new Thread(()=>
        {
            try{if(OperatingSystem.IsWindows())result=Execute(request);}catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InputException(failure.Message);
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static OneCWorkerResult Execute(OneCWorkerRequest request)
    {
        var builder=new SqlConnectionStringBuilder(request.ConnectionString){InitialCatalog=request.Database,Pooling=false};
        using var db=new SqlConnection(builder.ConnectionString);db.Open();
        var completed=false;
        try
        {
            var root=(OneCCom.Dispatch)OneCCom.Create(request.Settings.PlatformDirectory);
            root.Set("PoolCapacity",0);
            OneCUserSnapshot.SaveAndClear(db,request.JobId,CancellationToken.None).GetAwaiter().GetResult();
            var context=(OneCCom.Dispatch)root.Call("Connect",$"Srvr=\"{request.Settings.ClusterAddress}\";Ref=\"{request.Database}\";Usr=\"\";Pwd=\"\";WA=0;");
            // Keep this very connection alive while returning the original user storage.
            OneCUserSnapshot.Restore(db,request.JobId,CancellationToken.None).GetAwaiter().GetResult();
            var trust=(OneCCom.Dispatch)context.Call("NewObject","ОписаниеЗащитыОтОпасныхДействий");
            trust.Set("ПредупреждатьОбОпасныхДействиях",false);
            var processors=(OneCCom.Dispatch)context.Get("ExternalDataProcessors");
            var processor=(OneCCom.Dispatch)processors.Call("Create",request.ProcessorPath,false,trust);
            var written=request.Domains.Select(domain=>Convert.ToInt32(processor.Call("RewriteOsDomain",domain))).ToArray();
            completed=true;
            return new(written);
        }
        finally
        {
            try{OneCCom.ReleaseAll();}
            finally
            {
                if(!completed)OneCUserSnapshot.Restore(db,request.JobId,CancellationToken.None).GetAwaiter().GetResult();
                OneCUserSnapshot.Remove(db,request.JobId,CancellationToken.None).GetAwaiter().GetResult();
            }
        }
    }
}
