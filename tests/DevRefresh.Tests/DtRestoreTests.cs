using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevRefresh;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevRefresh.Tests;

public sealed class DtRestoreTests
{
    const string SqlPassword="SQL-synthetic-DT-password!";
    const string IbPassword="1C-synthetic-DT-password!";

    static async Task<HttpResponseMessage> Send(HttpClient client,HttpMethod method,string path,object body)
    {
        var session=(await client.GetFromJsonAsync<JsonObject>("/api/session"))!;
        using var request=new HttpRequestMessage(method,path){Content=JsonContent.Create(body)};
        request.Headers.Add("X-CSRF-TOKEN",session["csrfToken"]!.GetValue<string>());
        return await client.SendAsync(request);
    }
    static DtRestoreInput Dt(Factory app) => new(true,Path.Combine(app.DirectoryPath,"dt"),
        Path.Combine(app.DirectoryPath,"bin"),"","IB-admin",IbPassword);
    static SettingsInput Settings(Factory app,DtRestoreInput dt) => new("TEST-SQL","sql","sql_service",SqlPassword,
        Path.Combine(app.DirectoryPath,"bak"),Path.Combine(app.DirectoryPath,"bak"),false,DtRestore:dt);
    static async Task<HttpClient> Prepare(Factory app)
    {
        var client=app.Client();
        Assert.Equal(HttpStatusCode.OK,(await Send(client,HttpMethod.Post,"/api/setup",new SetupInput("admin","Synthetic-admin-password!",[]))).StatusCode);
        foreach(var folder in new[]{"dt","bin","bak"})Directory.CreateDirectory(Path.Combine(app.DirectoryPath,folder));
        // Only catalog/configuration tests use this placeholder. It is never executed.
        File.WriteAllText(Path.Combine(app.DirectoryPath,"bin","ibcmd.exe"),"not an executable");
        Assert.Equal(HttpStatusCode.OK,(await Send(client,HttpMethod.Put,"/api/admin/settings",Settings(app,Dt(app)))).StatusCode);
        return client;
    }

    [Theory]
    [InlineData("uho_ke.dt","uho_ke",null)]
    [InlineData("UHO_KE_18-11-2026.DT","uho_ke","2026-11-18")]
    public void DtUsesTheExistingNameAndDateRules(string name,string source,string? date)
    {
        var parsed=WorkflowRules.ParseBackupName(name,".dt");
        Assert.Equal(source,parsed!.Value.SourceKey);
        Assert.Equal(date,parsed.Value.BackupDate?.ToString("yyyy-MM-dd"));
        Assert.Null(WorkflowRules.ParseBackupName(name));
        Assert.Null(WorkflowRules.ParseBackupName("../uho_ke.dt",".dt"));
        Assert.Null(WorkflowRules.ParseBackupName("uho_ke_31-02-2026.dt",".dt"));
    }

    [Fact]
    public async Task CatalogIsSeparateUsesAliasAndChoosesNewestValidDt()
    {
        using var app=new Factory();using var client=await Prepare(app);
        var folder=Path.Combine(app.DirectoryPath,"dt");
        foreach(var file in new[]{"bp_17-11-2026.dt","BP_18-11-2026.DT","bp_19-11-2026.bak","other.dt"})
            File.WriteAllText(Path.Combine(folder,file),"synthetic dump");
        File.WriteAllText(Path.Combine(folder,"bp_20-11-2026.dt"),"");
        File.WriteAllText(Path.Combine(app.DirectoryPath,"bak","bp_21-11-2026.dt"),"wrong directory");
        Assert.Equal(HttpStatusCode.OK,(await Send(client,HttpMethod.Put,"/api/admin/reduction/backup-aliases/bp_gm",new BackupAliasInput("bp"))).StatusCode);
        var settings=app.Services.GetRequiredService<Store>().GetSettings()!;
        Assert.Equal("BP_18-11-2026.DT",DtRestoreConfiguration.RequireLatest(settings,"bp").Name);
        var rows=(await client.GetFromJsonAsync<JsonObject>("/api/databases"))!["databases"]!.AsArray();
        var row=rows.First(x=>x!["name"]!.GetValue<string>()=="bp_gm_dev_demo_owner")!;
        Assert.Equal("BP_18-11-2026.DT",row["dtBackup"]!["name"]!.GetValue<string>());
        Assert.Null(row["backup"]);
    }

    [Fact]
    public async Task PasswordsAreEncryptedPreservedAndNeverReturned()
    {
        using var app=new Factory();using var client=await Prepare(app);
        var service=app.Services.GetRequiredService<SettingsService>();
        var store=app.Services.GetRequiredService<Store>();
        var previous=store.GetSettings()!;
        Assert.NotEqual(IbPassword,previous.DtRestore!.ProtectedInfobasePassword);
        var input=Settings(app,Dt(app) with{InfobasePassword=null}) with{Password=null};
        Assert.Equal(HttpStatusCode.OK,(await Send(client,HttpMethod.Put,"/api/admin/settings",input)).StatusCode);
        Assert.Equal(previous.DtRestore.ProtectedInfobasePassword,store.GetSettings()!.DtRestore!.ProtectedInfobasePassword);
        var response=await client.GetStringAsync("/api/admin/settings");
        Assert.DoesNotContain(IbPassword,response);Assert.DoesNotContain(SqlPassword,response);
        Assert.DoesNotContain(previous.DtRestore.ProtectedInfobasePassword,response);
        // Older settings clients omitting the entire DT object preserve it.
        Assert.Equal(previous.DtRestore,service.Prepare(input with{DtRestore=null}).DtRestore);
        var cleared=service.Prepare(input with{DtRestore=input.DtRestore! with{ClearInfobasePassword=true}});
        Assert.Empty(cleared.DtRestore!.ProtectedInfobasePassword);
        Assert.Empty(service.Prepare(input with{DtRestore=input.DtRestore! with{InfobaseUser="different"}}).DtRestore!.ProtectedInfobasePassword);
    }

    [Fact]
    public async Task SeparateSqlCredentialsAndArgumentsPreserveSpecialCharacters()
    {
        using var app=new Factory();using var client=await Prepare(app);
        var service=app.Services.GetRequiredService<SettingsService>();
        var input=Settings(app,Dt(app) with{SqlUsername="dt_sql",SqlPassword="DT sql ' & password!"}) with{Authentication="windows",Password=null};
        var settings=service.Prepare(input);
        var runner=app.Services.GetRequiredService<IbcmdRestore>();
        var archive=Path.Combine(app.DirectoryPath,"dt","bp_18-11-2026.dt");
        var launch=runner.Prepare(settings,"bp_gm_dev_demo_owner",archive,Path.Combine(app.DirectoryPath,"work's folder"),true,new(8,3,27,2214));
        Assert.Equal("DT sql ' & password!",launch.SqlPassword);
        Assert.Contains("--db-user=dt_sql",launch.Start.ArgumentList);
        Assert.Contains("--db-name=bp_gm_dev_demo_owner",launch.Start.ArgumentList);
        Assert.Contains("--request-db-pwd",launch.Start.ArgumentList);
        Assert.DoesNotContain(launch.Start.ArgumentList,x=>x.Contains(launch.SqlPassword));
        Assert.Contains("--password="+IbPassword,launch.Start.ArgumentList);
        Assert.Contains("--force",launch.Start.ArgumentList);
        Assert.Contains(launch.Start.ArgumentList,x=>x.StartsWith("--session-terminate-message="));
        Assert.Equal(archive,launch.Start.ArgumentList.Last());
        var old=runner.Prepare(settings,"bp_gm_dev_demo_owner",archive,"C:\\work",true,new(8,3,25,0));
        Assert.DoesNotContain(old.Start.ArgumentList,x=>x.StartsWith("--session-terminate-message="));
        Assert.DoesNotContain(runner.Prepare(settings,"bp_gm_dev_demo_owner",archive,"C:\\work",false,new(8,3,27,0)).Start.ArgumentList,x=>x=="--force");
        var fallback=runner.Prepare(app.Services.GetRequiredService<Store>().GetSettings()!,"bp_gm_dev_demo_owner",archive,"C:\\work",false,new(8,3,27,0));
        Assert.Equal(SqlPassword,fallback.SqlPassword);
    }

    [Fact]
    public async Task QueueAcceptsOneSourceAndScheduledDtResolvesTheArchiveAtStart()
    {
        using var app=new Factory();using var client=await Prepare(app);
        File.WriteAllText(Path.Combine(app.DirectoryPath,"dt","bp_gm.dt"),"synthetic dump");
        var request=new RefreshInput("bp_gm_dev_demo_owner",false,false,null,null,true,true,Dt:true);
        foreach(var mixed in new[]{request with{Shrink=true},request with{SourceDatabase="bp_gm_dev_other"},request with{BackupName="bp_gm.bak"}})
            Assert.Equal(HttpStatusCode.BadRequest,(await Send(client,HttpMethod.Post,"/api/refresh",mixed)).StatusCode);
        var accepted=await Send(client,HttpMethod.Post,"/api/refresh",request);
        Assert.Equal(HttpStatusCode.Accepted,accepted.StatusCode);
        var publicJob=(await accepted.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(publicJob["useDt"]!.GetValue<bool>());
        Assert.DoesNotContain(IbPassword,publicJob.ToJsonString());
        var job=app.Services.GetRequiredService<Store>().GetJob(publicJob["id"]!.GetValue<string>())!;
        Assert.True(job.Request.UseDt);Assert.True(job.Request.ResolveLatestBackup);Assert.Empty(job.Request.BackupName);
        File.Delete(Path.Combine(app.DirectoryPath,"dt","bp_gm.dt"));
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(client,HttpMethod.Post,"/api/refresh",request)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,(await Send(client,HttpMethod.Post,"/api/refresh",request with{ScheduledAt=DateTimeOffset.UtcNow.AddHours(1)})).StatusCode);
        using var domain=app.Client();domain.DefaultRequestHeaders.Add("Test-Domain","CORP\\mallory");
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(domain,HttpMethod.Post,"/api/refresh",request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await Send(domain,HttpMethod.Post,"/api/admin/ibcmd/test",Settings(app,Dt(app)))).StatusCode);
    }

    [Fact]
    public async Task InvalidConfigurationAndDisabledDtCannotQueueWork()
    {
        using var app=new Factory();using var client=await Prepare(app);
        var service=app.Services.GetRequiredService<SettingsService>();
        Assert.Throws<InputException>(()=>service.Prepare(Settings(app,Dt(app) with{BackupDirectory=Path.Combine(app.DirectoryPath,"bak")})));
        Assert.Throws<InputException>(()=>service.Prepare(Settings(app,Dt(app) with{IbcmdDirectory="relative"})));
        Assert.Throws<InputException>(()=>service.Prepare(Settings(app,Dt(app) with{TimeoutMinutes=0})));
        Assert.Throws<InputException>(()=>service.Prepare(Settings(app,Dt(app) with{SqlPassword="line\nbreak",SqlUsername="bad"})));
        var disabled=Settings(app,Dt(app) with{Enabled=false});
        Assert.Equal(HttpStatusCode.OK,(await Send(client,HttpMethod.Put,"/api/admin/settings",disabled)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(client,HttpMethod.Post,"/api/refresh",
            new RefreshInput("bp_gm_dev_demo_owner",false,false,null,null,true,true,ScheduledAt:DateTimeOffset.UtcNow.AddHours(1),Dt:true))).StatusCode);
    }

    [Fact]
    public async Task NewPersistedJobFormatCannotBeOpenedByABakOnlyWorker()
    {
        using var app=new Factory();using var client=await Prepare(app);
        var path=Path.Combine(app.DirectoryPath,"devrefresh.sqlite");
        Assert.Equal(11,DatabaseMigrator.ReadVersion(path));
        Assert.Throws<InvalidOperationException>(()=>DatabaseMigrator.Ensure(path,DatabaseMigrator.Migrations.Take(10).ToArray()));
        var old=JsonSerializer.Deserialize<WorkflowRequest>("""{"Settings":{"Server":"s","Authentication":"windows","Username":"","ProtectedPassword":"","BackupDirectory":"C:\\bak","SqlBackupDirectory":"C:\\bak","TrustServerCertificate":false},"BackupName":"x.bak","Profile":null,"Rules":[],"Shrink":false,"ConfirmDisconnect":false}""")!;
        Assert.False(old.UseDt);
    }

    static ProcessStartInfo PowerShell(string script)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"));
        foreach(var argument in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})
            start.ArgumentList.Add(argument);
        return start;
    }

    [Fact]
    public async Task ProcessDrainsBothStreamsPassesStdinRedactsSecretsAndReportsExitCode()
    {
        var runner=new IbcmdProcessRunner();
        var start=PowerShell("$p=[Console]::ReadLine(); for($i=0;$i -lt 1200;$i++){[Console]::Error.WriteLine('diagnostic line '+$i)}; [Console]::WriteLine($p); [Console]::Error.WriteLine('"+IbPassword+"'); exit 9");
        var result=await runner.Run(start,SqlPassword,TimeSpan.FromSeconds(15),[SqlPassword,IbPassword],CancellationToken.None);
        Assert.Equal(9,result.ExitCode);Assert.DoesNotContain(SqlPassword,result.Output);Assert.DoesNotContain(IbPassword,result.Output);
        Assert.Contains("[скрыто]",result.Output);Assert.True(result.Output.Length<17000);
    }

    [Fact]
    public async Task EarlyProcessExitPreservesItsFailureInsteadOfAnStdinPipeError()
    {
        var result=await new IbcmdProcessRunner().Run(PowerShell("[Console]::Error.WriteLine('early diagnostic'); exit 17"),
            new string('x',32768),TimeSpan.FromSeconds(10),[],CancellationToken.None);
        Assert.Equal(17,result.ExitCode);Assert.Contains("early diagnostic",result.Output);
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationStopTheChildProcess()
    {
        var runner=new IbcmdProcessRunner();
        var timer=Stopwatch.StartNew();
        var timeout=await Assert.ThrowsAsync<InputException>(()=>runner.Run(PowerShell("Start-Sleep -Seconds 30"),null,TimeSpan.FromSeconds(1),[],CancellationToken.None));
        Assert.Contains("таймаут",timeout.Message);Assert.True(timer.Elapsed<TimeSpan.FromSeconds(10));
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>runner.Run(PowerShell("Start-Sleep -Seconds 30"),null,TimeSpan.FromSeconds(15),[],cancellation.Token));
    }
}
