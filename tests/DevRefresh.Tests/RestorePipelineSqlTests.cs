using DevRefresh;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DevRefresh.Tests;

public sealed class DomainSqlFactAttribute:FactAttribute
{
    public DomainSqlFactAttribute(){if(Environment.GetEnvironmentVariable("DEVREFRESH_TEST_DOMAIN_SQL")!="1")Skip="Set DEVREFRESH_TEST_DOMAIN_SQL=1 to test isolated databases on local SQL Server.";}
}

public sealed class RestorePipelineSqlTests
{
    sealed class Fixture:IAsyncDisposable
    {
        public string Database {get;}="drptest_dev_"+Guid.NewGuid().ToString("N");
        static string ConnectionString(string database)=>new SqlConnectionStringBuilder
            {DataSource="localhost",InitialCatalog=database,IntegratedSecurity=true,TrustServerCertificate=true,Pooling=false,ConnectTimeout=5}.ConnectionString;
        public async Task<SqlConnection> Open(){var cn=new SqlConnection(ConnectionString(Database));await cn.OpenAsync();return cn;}
        public static async Task<Fixture> Create()
        {
            var fixture=new Fixture();
            await using var master=new SqlConnection(ConnectionString("master"));await master.OpenAsync();
            await RestoreSql.Execute(master,$"CREATE DATABASE {RestoreSql.Q(fixture.Database)}");
            try
            {
                await using var db=await fixture.Open();
                await RestoreSql.Execute(db,"""
                    CREATE TABLE dbo.v8users(ID binary(16) PRIMARY KEY,Name nvarchar(64),OSName nvarchar(128),
                        Changed datetime2,Data varbinary(max),RolesID int,EAuth binary(1));
                    CREATE TABLE dbo.Params(FileName nvarchar(128),BinaryData varbinary(max));
                    INSERT dbo.Params VALUES(N'users.usr',0x010203040506);
                    INSERT dbo.v8users VALUES(0x01,N'first',N'\\OLD\first','4026-11-18',0x010203,7,0x01),
                        (0x02,N'second',N'\\OTHER\second','4026-11-18',0x040506,8,0x00),
                        (0x03,N'existing',N'\\CORP\existing','4026-11-18',0x070809,9,0x01),
                        (0x04,N'without-os',NULL,'4026-11-18',0x0A0B0C,10,0x01);
                    """);
                return fixture;
            }
            catch{await fixture.DisposeAsync();throw;}
        }
        public RestorePipeline Pipeline(params string[] domains)=>new("localhost",Database,"full",
            domains.Select(x=>new RestoreOperation(RestorePipelineRules.WindowsDomain,x)).ToArray(),"now","test");
        public async ValueTask DisposeAsync()
        {
            // Never drop any database except this exact, newly generated test name.
            if(!Database.StartsWith("drptest_dev_",StringComparison.Ordinal) || !Guid.TryParseExact(Database[12..],"N",out _))
                throw new InvalidOperationException("Unexpected test database name.");
            await using var master=new SqlConnection(ConnectionString("master"));await master.OpenAsync();
            await RestoreSql.Execute(master,$"ALTER DATABASE {RestoreSql.Q(Database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {RestoreSql.Q(Database)};");
        }
    }
    static async Task<string> Fingerprint(SqlConnection db) => await RestoreSql.Scalar<string>(db,"""
        SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),
            (SELECT ID,Name,Changed,Data,RolesID,EAuth FROM dbo.v8users ORDER BY ID FOR JSON PATH))),2)
            + CONVERT(varchar(64),HASHBYTES('SHA2_256',(SELECT BinaryData FROM dbo.Params WHERE FileName='users.usr')),2)
        """);

    [DomainSqlFact]
    public async Task SqlChangesOnlyOsNameAndIsIdempotent()
    {
        await using var fixture=await Fixture.Create();await using var db=await fixture.Open();
        var before=await Fingerprint(db);var executor=new RestorePipelineExecutor();
        var result=Assert.Single(await executor.Apply(db,fixture.Pipeline("CORP"),_=>{},CancellationToken.None));
        Assert.Equal(2,result.Changed);Assert.Equal(1,result.Unchanged);Assert.Equal(before,await Fingerprint(db));
        Assert.Equal(@"\\CORP\first",await RestoreSql.Scalar<string>(db,"SELECT OSName FROM dbo.v8users WHERE Name='first'"));
        Assert.Equal(0,Assert.Single(await executor.Apply(db,fixture.Pipeline("CORP"),_=>{},CancellationToken.None)).Changed);
    }
    [DomainSqlFact]
    public async Task CollisionDoesNotWriteAnyRows()
    {
        await using var fixture=await Fixture.Create();await using var db=await fixture.Open();
        await RestoreSql.Execute(db,"UPDATE dbo.v8users SET OSName=N'\\CORP\first' WHERE Name='existing'");
        await Assert.ThrowsAsync<InputException>(()=>new RestorePipelineExecutor().Apply(db,fixture.Pipeline("CORP"),_=>{},CancellationToken.None));
        Assert.Equal(@"\\OLD\first",await RestoreSql.Scalar<string>(db,"SELECT OSName FROM dbo.v8users WHERE Name='first'"));
    }
    [DomainSqlFact]
    public async Task FailureOfTheNextOperationRollsBackTheWholeChain()
    {
        await using var fixture=await Fixture.Create();await using var db=await fixture.Open();
        await Assert.ThrowsAsync<InputException>(()=>new RestorePipelineExecutor().Apply(db,fixture.Pipeline("CORP",new string('D',128)),_=>{},CancellationToken.None));
        Assert.Equal(@"\\OLD\first",await RestoreSql.Scalar<string>(db,"SELECT OSName FROM dbo.v8users WHERE Name='first'"));
        Assert.Equal(@"\\OTHER\second",await RestoreSql.Scalar<string>(db,"SELECT OSName FROM dbo.v8users WHERE Name='second'"));
    }
}
