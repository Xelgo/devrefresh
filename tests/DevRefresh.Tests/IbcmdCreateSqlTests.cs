using System.Diagnostics;
using DevRefresh;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevRefresh.Tests;

public sealed class IbcmdSqlFactAttribute:FactAttribute
{
    public IbcmdSqlFactAttribute(){if(Environment.GetEnvironmentVariable("DEVREFRESH_TEST_IBCMD_SQL")!="1")Skip="Set DEVREFRESH_TEST_IBCMD_SQL=1 for isolated IBCMD/MSSQL integration tests.";}
}
public sealed class IbcmdCreateSqlTests
{
    const string Bin=@"C:\Program Files\1cv8\8.3.27.2214\bin";
    sealed class SqlFixture:IAsyncDisposable
    {
        readonly string id=Guid.NewGuid().ToString("N");
        public string Database=>"drpcreate_dev_"+id;
        public string Login=>"drpcreate_"+id;
        public string Password {get;}="Dt!"+Guid.NewGuid().ToString("N")+"aB9";
        bool loginCreated,databaseCreated;
        public async Task<SqlConnection> Open(string database="master")
        {var cn=new SqlConnection(new SqlConnectionStringBuilder{DataSource="localhost",InitialCatalog=database,IntegratedSecurity=true,TrustServerCertificate=true,Pooling=false}.ConnectionString);await cn.OpenAsync();return cn;}
        public async Task Create(bool canCreate)
        {
            await using var master=await Open();
            await RestoreSql.Execute(master,$"CREATE LOGIN {RestoreSql.Q(Login)} WITH PASSWORD={RestoreSql.L(Password)},CHECK_POLICY=OFF,CHECK_EXPIRATION=OFF");loginCreated=true;
            if(canCreate)await RestoreSql.Execute(master,$"GRANT CREATE ANY DATABASE TO {RestoreSql.Q(Login)}");
            await RestoreSql.Execute(master,$"CREATE DATABASE {RestoreSql.Q(Database)}");databaseCreated=true;
            await RestoreSql.Execute(master,$"ALTER AUTHORIZATION ON DATABASE::{RestoreSql.Q(Database)} TO {RestoreSql.Q(Login)}");
            await using var db=await Open(Database);
            await RestoreSql.Execute(db,"CREATE TABLE dbo.DevRefreshOldDatabaseMarker(id int); INSERT dbo.DevRefreshOldDatabaseMarker VALUES(1)");
        }
        public async ValueTask DisposeAsync()
        {
            if(!Guid.TryParseExact(id,"N",out _))throw new InvalidOperationException("Unexpected fixture name.");
            await using var master=await Open();
            if(databaseCreated)await RestoreSql.Execute(master,$"IF DB_ID({RestoreSql.L(Database)}) IS NOT NULL BEGIN ALTER DATABASE {RestoreSql.Q(Database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {RestoreSql.Q(Database)}; END");
            if(loginCreated)await RestoreSql.Execute(master,$"DROP LOGIN {RestoreSql.Q(Login)}");
        }
    }
    static async Task RunCommand(string[] arguments)
    {
        var start=new ProcessStartInfo(Path.Combine(Bin,"ibcmd.exe"));foreach(var arg in arguments)start.ArgumentList.Add(arg);
        var result=await new IbcmdProcessRunner().Run(start,null,TimeSpan.FromSeconds(30),[],CancellationToken.None);
        Assert.True(result.ExitCode==0,result.Output);
    }
    static async Task<WorkflowJob> RunJob(Factory app,SqlFixture fixture,bool validDump)
    {
        var folder=Path.Combine(app.DirectoryPath,"dt");Directory.CreateDirectory(folder);
        var dump=Path.Combine(folder,"drpcreate.dt");
        if(validDump)
        {
            var file=Path.Combine(app.DirectoryPath,"file-source");var data=Path.Combine(app.DirectoryPath,"file-data");
            await RunCommand(["infobase","create","--db-path="+file,"--data="+data,"--force"]);
            await RunCommand(["infobase","dump","--db-path="+file,"--data="+data,dump]);
        }
        else File.WriteAllText(dump,"not executed when permission preflight fails");
        var bak=Path.Combine(app.DirectoryPath,"bak");Directory.CreateDirectory(bak);
        var settings=app.Services.GetRequiredService<SettingsService>().Prepare(new("localhost","sql",fixture.Login,fixture.Password,
            bak,bak,true,RunDbccCheckDb:true,DtRestore:new(true,folder,Bin,"")));
        var store=app.Services.GetRequiredService<Store>();store.SaveSettings(settings,"integration-test");
        var job=store.CreateJob("refresh","admin","drpcreate",fixture.Database,new(settings,"",null,[],false,true,ResolveLatestBackup:true,UseDt:true));
        app.Services.GetRequiredService<IConfiguration>()["Workflows:Execute"]="true";
        using var worker=ActivatorUtilities.CreateInstance<WorkflowService>(app.Services);await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while(store.GetJob(job.Id)!.Status is "Queued" or "Running")await Task.Delay(50,timeout.Token);
            return store.GetJob(job.Id)!;
        }
        finally{await worker.StopAsync(CancellationToken.None);}
    }
    [IbcmdSqlFact]
    public async Task RealDtRecreatesSqlTargetThroughCreateWithoutInfobaseCredentialsOrTemporaryBak()
    {
        await using var fixture=new SqlFixture();await fixture.Create(true);
        using var app=new Factory();using var initialize=app.Client();
        var job=await RunJob(app,fixture,true);Assert.True(job.Status=="Succeeded",job.Error);
        await using var db=await fixture.Open(fixture.Database);
        Assert.Equal(0,await RestoreSql.Scalar<int>(db,"SELECT COUNT(*) FROM sys.tables WHERE name='DevRefreshOldDatabaseMarker'"));
        Assert.Equal(1,await RestoreSql.Scalar<int>(db,"SELECT COUNT(*) FROM sys.tables WHERE name='Config'"));
        Assert.Empty(Directory.EnumerateFiles(app.DirectoryPath,"*.bak",SearchOption.AllDirectories));
        Assert.Contains("IbcmdVersion",job.Result);Assert.NotNull(job.ReadyAt);
    }
    [IbcmdSqlFact]
    public async Task MissingSqlCreatePermissionFailsBeforeDeletingTheTarget()
    {
        await using var fixture=new SqlFixture();await fixture.Create(false);
        using var app=new Factory();using var initialize=app.Client();
        var job=await RunJob(app,fixture,false);Assert.Equal("Failed",job.Status);Assert.Contains("не имеет права создавать",job.Error);
        await using var db=await fixture.Open(fixture.Database);
        Assert.Equal(1,await RestoreSql.Scalar<int>(db,"SELECT COUNT(*) FROM dbo.DevRefreshOldDatabaseMarker"));Assert.Null(job.ReadyAt);
    }
}
