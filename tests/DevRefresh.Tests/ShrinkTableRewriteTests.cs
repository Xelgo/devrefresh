using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevRefresh.Tests;

public sealed class LocalDbFactAttribute : FactAttribute
{
    public LocalDbFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DEVREFRESH_TEST_LOCALDB") != "1")
            Skip = "Set DEVREFRESH_TEST_LOCALDB=1 to test against SQL Server LocalDB.";
    }
}

public sealed class ShrinkTableRewriteTests
{
    [LocalDbFact]
    public async Task RewriteMatchesDeleteAndPreservesTableObjects()
    {
        await InStage(async (settings, sql, connection, analysis) =>
        {
            await SeedRows(connection);
            var objects = await ReadObjects(connection);

            var baseline = await Apply(settings, sql, analysis, rewrite: false, shrinkBuild: false);
            await RestoreSql.Execute(connection, """
                SELECT * INTO #ExpectedMain FROM dbo.Movements;
                SELECT * INTO #ExpectedDetail FROM dbo.Dimensions;
                SELECT * INTO #ExpectedTotals FROM dbo.Totals;
                """);

            await SeedRows(connection);
            var stages = new List<string>();
            var optimized = await Apply(settings, sql, analysis, rewrite: true, progress: stages.Add);

            Assert.Equal(baseline.Deleted, optimized.Deleted);
            Assert.Equal(768_000, optimized.TotalRows);
            Assert.Contains(stages, stage => stage.StartsWith("Возврат сохранённых строк · main", StringComparison.Ordinal));
            Assert.Contains(stages, stage => stage.StartsWith("Возврат сохранённых строк · detail", StringComparison.Ordinal));
            Assert.Contains("Очистка таблицы · totals", stages);
            Assert.Equal(objects, await ReadObjects(connection));
            Assert.Equal(0, await WorkTableCount(connection));
            var differences = await RestoreSql.Scalar<int>(connection, """
                SELECT CASE WHEN EXISTS (SELECT * FROM dbo.Movements EXCEPT SELECT * FROM #ExpectedMain)
                    OR EXISTS (SELECT * FROM #ExpectedMain EXCEPT SELECT * FROM dbo.Movements)
                    OR EXISTS (SELECT * FROM dbo.Dimensions EXCEPT SELECT * FROM #ExpectedDetail)
                    OR EXISTS (SELECT * FROM #ExpectedDetail EXCEPT SELECT * FROM dbo.Dimensions)
                    OR EXISTS (SELECT * FROM dbo.Totals EXCEPT SELECT * FROM #ExpectedTotals)
                    OR EXISTS (SELECT * FROM #ExpectedTotals EXCEPT SELECT * FROM dbo.Totals)
                    THEN 1 ELSE 0 END
                """);
            Assert.Equal(0, differences);
        });
    }

    [LocalDbFact]
    public async Task InterruptionAfterTruncateRollsBackAndDropsWorkingCopies()
    {
        await InStage(async (settings, sql, connection, analysis) =>
        {
            await SeedRows(connection);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Apply(settings, sql, analysis, rewrite: true, token: cancellation.Token, progress: stage =>
                {
                    if (stage.StartsWith("Возврат сохранённых строк · main", StringComparison.Ordinal))
                        cancellation.Cancel();
                }));
            Assert.Equal(300_000, await RestoreSql.Scalar<long>(connection, "SELECT COUNT_BIG(*) FROM dbo.Movements"));
            Assert.Equal(72_000, await RestoreSql.Scalar<long>(connection, "SELECT COUNT_BIG(*) FROM dbo.Dimensions"));
            Assert.Equal(0, await RestoreSql.Scalar<long>(connection, "SELECT COUNT_BIG(*) FROM dbo.Totals"));
            Assert.Equal(0, await WorkTableCount(connection));
        });
    }

    [LocalDbFact]
    public async Task LowDeleteShareAndIdentityTableUseOrdinaryDelete()
    {
        await InStage(async (settings, sql, connection, analysis) =>
        {
            await SeedRows(connection);
            await RestoreSql.Execute(connection, """
                UPDATE dbo.Movements SET _Period='2025-01-01' WHERE Id>150000;
                ALTER TABLE dbo.Totals ADD SequenceNumber int IDENTITY NOT NULL;
                """);
            var stages = new List<string>();
            var result = await Apply(settings, sql, analysis, rewrite: true, progress: stages.Add);
            Assert.Equal(150_000, result.Deleted.Single(item => item.Object == "main").Rows);
            Assert.DoesNotContain(stages, stage => stage.StartsWith("Возврат сохранённых строк · main", StringComparison.Ordinal));
            Assert.DoesNotContain("Очистка таблицы · totals", stages);
            Assert.Equal(300_000, await RestoreSql.Scalar<int>(connection, "SELECT CONVERT(int,IDENT_CURRENT('dbo.Totals'))"));
            Assert.Equal(0, await WorkTableCount(connection));
        });
    }

    [LocalDbFact]
    public async Task CancellationLeavesOnlyCompletedDeleteBatchesCommitted()
    {
        await InStage(async (settings, sql, connection, analysis) =>
        {
            foreach (var stagePrefix in new[] { "Удаление связанных строк · detail · ", "Удаление выбранных строк · totals · " })
            {
                await SeedRows(connection);
                using var cancellation = new CancellationTokenSource();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    Apply(settings, sql, analysis, rewrite: false, token: cancellation.Token, progress: stage =>
                    {
                        if (stage.StartsWith(stagePrefix,StringComparison.Ordinal))
                            cancellation.Cancel();
                    }));
                bool cancelledOnDetail = stagePrefix.Contains("detail",StringComparison.Ordinal);
                Assert.Equal(cancelledOnDetail ? 250_000 : 72_000,
                    await RestoreSql.Scalar<long>(connection, "SELECT COUNT_BIG(*) FROM dbo.Dimensions"));
                Assert.Equal(cancelledOnDetail ? 300_000 : 250_000,
                    await RestoreSql.Scalar<long>(connection, "SELECT COUNT_BIG(*) FROM dbo.Totals"));
                Assert.Equal(300_000,
                    await RestoreSql.Scalar<long>(connection, "SELECT COUNT_BIG(*) FROM dbo.Movements"));
            }
        });
    }

    [LocalDbFact]
    public async Task SliceIsRebuiltAfterDeleteKeyBatchesAreConsumed()
    {
        await InStage(async (settings, sql, connection, analysis) =>
        {
            await SeedRows(connection);
            await RestoreSql.Execute(connection, "SELECT * INTO dbo.SliceLast FROM dbo.Movements");
            var root = analysis.Tables[0] with
            {
                Kind = "РегистрСведений",
                Fields = [new("Измерение","Payload","Dimension","","")]
            };
            var slice = root with
            {
                Key = "slice", Table = "SliceLast", Role = "SliceLast", DateColumn = null, CanSelect = false
            };
            var result = await Apply(settings, sql, analysis with
            {
                Tables = [root, .. analysis.Tables[1..], slice]
            }, rewrite: false);
            Assert.Contains(result.Deleted,item => item.Table=="SliceLast");
            Assert.Equal(60_000,await RestoreSql.Scalar<long>(connection,"SELECT COUNT_BIG(*) FROM dbo.SliceLast"));
            Assert.Equal(240_001,await RestoreSql.Scalar<int>(connection,"SELECT MIN(Id) FROM dbo.SliceLast"));
        });
    }

    [LocalDbFact]
    public async Task BinaryRewriteMatchesBatchedDeleteForImagePayloads()
    {
        await InStage(async (settings, sql, connection, analysis) =>
        {
            analysis = await SeedBinaryData(connection,analysis,"image");
            var baseline = await ApplyBinaryData(settings,sql,analysis,rewrite:false);
            var snapshot = await RestoreSql.Query(connection, """
                SELECT OBJECT_ID(N'dbo.BinaryData') AS ObjectId,
                       (SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.BinaryData')) AS IndexCount
                """);
            await RestoreSql.Execute(connection, """
                SELECT f_key, Part, CONVERT(varbinary(max),f_data) AS Payload INTO #ExpectedBinary FROM dbo.BinaryData
                """);

            await RestoreSql.Execute(connection,"DROP TABLE dbo.BinaryData, dbo.Files, dbo.Surviving");
            await SeedBinaryData(connection,analysis,"image");
            var objectId = await RestoreSql.Scalar<int>(connection,"SELECT OBJECT_ID(N'dbo.BinaryData')");
            var stages = new List<string>();
            var optimized = await ApplyBinaryData(settings,sql,analysis,rewrite:true,progress:stages.Add);

            Assert.Equal(new BinaryDataCleanupReport(2721,20,2700,270_000,2_160_000),baseline.BinaryDataCleanup);
            Assert.Equal(baseline.BinaryDataCleanup,optimized.BinaryDataCleanup);
            Assert.True(stages.Any(stage=>stage.StartsWith("Возврат сохранённых строк · BinaryData",StringComparison.Ordinal)),
                string.Join(Environment.NewLine,stages));
            Assert.Equal(objectId,await RestoreSql.Scalar<int>(connection,"SELECT OBJECT_ID(N'dbo.BinaryData')"));
            Assert.Equal(snapshot[0]["IndexCount"],await RestoreSql.Scalar<int>(connection,
                "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.BinaryData')"));
            Assert.Equal(30_000,await RestoreSql.Scalar<long>(connection,"SELECT COUNT_BIG(*) FROM dbo.BinaryData"));
            Assert.Equal(0,await WorkTableCount(connection));
            Assert.Equal(0,await RestoreSql.Scalar<int>(connection, """
                SELECT CASE WHEN EXISTS (
                    SELECT f_key,Part,CONVERT(varbinary(max),f_data) FROM dbo.BinaryData EXCEPT SELECT * FROM #ExpectedBinary
                ) OR EXISTS (
                    SELECT * FROM #ExpectedBinary EXCEPT SELECT f_key,Part,CONVERT(varbinary(max),f_data) FROM dbo.BinaryData
                ) THEN 1 ELSE 0 END
                """));
        });
    }

    [LocalDbFact]
    public async Task BinaryRewriteFallsBackWhenMostPayloadBytesWouldSurvive()
    {
        await InStage(async (settings, sql, connection, analysis) =>
        {
            analysis = await SeedBinaryData(connection,analysis,"varbinary(max)");
            await RestoreSql.Execute(connection, """
                UPDATE dbo.BinaryData SET f_data=CONVERT(varbinary(max),REPLICATE('x',120))
                WHERE f_key>CONVERT(binary(16),2700)
                """);
            var stages = new List<string>();
            var result = await ApplyBinaryData(settings,sql,analysis,rewrite:true,progress:stages.Add);
            Assert.Equal(270_000,result.BinaryDataCleanup?.DeletedBlocks);
            Assert.Contains("Оценка объёма очистки BinaryData",stages);
            Assert.Contains("Удаление блоков BinaryData",stages);
            Assert.DoesNotContain(stages,stage=>stage.StartsWith("Возврат сохранённых строк",StringComparison.Ordinal));
        });
    }

    [LocalDbFact]
    public async Task BinaryRewriteCancellationRestoresAllBlocks()
    {
        await InStage(async (settings, sql, connection, analysis) =>
        {
            analysis = await SeedBinaryData(connection,analysis,"varbinary(max)");
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ApplyBinaryData(settings,sql,analysis,rewrite:true,token:cancellation.Token,progress:stage=>
                {
                    if (stage.StartsWith("Возврат сохранённых строк · BinaryData",StringComparison.Ordinal))
                        cancellation.Cancel();
                }));
            Assert.Equal(300_000,await RestoreSql.Scalar<long>(connection,"SELECT COUNT_BIG(*) FROM dbo.BinaryData"));
            Assert.Equal(0,await WorkTableCount(connection));
        });
    }

    private static async Task<AnalysisData> SeedBinaryData(SqlConnection connection, AnalysisData analysis, string dataType)
    {
        await RestoreSql.Execute(connection,$"""
            CREATE TABLE dbo.BinaryData (
                f_key binary(16) NOT NULL, Part int NOT NULL, f_data {dataType} NULL,
                PRIMARY KEY (f_key,Part)
            );
            CREATE INDEX IX_BinaryData_Part ON dbo.BinaryData(Part);
            CREATE TABLE dbo.Files (Id int NOT NULL PRIMARY KEY, Reference varbinary(32) NOT NULL);
            CREATE TABLE dbo.Surviving (Reference varbinary(32) NOT NULL);
            ;WITH Numbers AS (
                SELECT TOP (300000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS Id
                FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN (VALUES (1),(2),(3),(4)) c(n)
            )
            INSERT dbo.BinaryData (f_key,Part,f_data)
            SELECT CONVERT(binary(16),Id/100+1),Id%100,CONVERT(binary(8),Id)
            FROM Numbers;
            INSERT dbo.Files (Id,Reference)
            SELECT PartKey,0x53544F52484452000000000000000000 + f_key
            FROM (SELECT DISTINCT CONVERT(int,f_key) AS PartKey,f_key FROM dbo.BinaryData
                  WHERE f_key<=CONVERT(binary(16),2720)) keys;
            INSERT dbo.Files (Id,Reference)
            VALUES (4000,0x53544F52484452000000000000000000 + CONVERT(binary(16),4000));
            INSERT dbo.Surviving SELECT Reference FROM dbo.Files WHERE Id BETWEEN 2701 AND 2720;
            """);
        var files = Table("files","Files","Main","files",period:false,nullable:false,keys:["Id"]) with
        {
            Kind="Справочник", Rows=2721,
            Columns=[new("Id","int",4,false,false,false),new("Reference","varbinary",32,false,false,false)]
        };
        var binary = Table("binary","BinaryData","System","",period:false,nullable:false,keys:["f_key","Part"]) with
        {
            Columns=[new("f_key","binary",16,false,false,false),new("Part","int",4,false,false,false),
                new("f_data",dataType,-1,true,false,false)]
        };
        return analysis with { Tables=[files,binary] };
    }

    private static Task<ReductionResult> ApplyBinaryData(SqlSettings settings, RestoreSql sql, AnalysisData analysis,
        bool rewrite, Action<string>? progress = null, CancellationToken token = default)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Shrink:RewriteTables"] = rewrite.ToString(),
            ["SqlDiagnostics:Enabled"] = "false"
        }).Build();
        var executor = new ReductionExecutor(sql,NullLogger<ReductionExecutor>.Instance,config);
        return executor.Apply(settings,"DevRefreshStage_"+analysis.Id,analysis,[new("files",null)],true,
            progress ?? (_=>{}),token,jobId:analysis.Id,cleanBinaryData:true);
    }

    private static Task<int> WorkTableCount(SqlConnection connection) =>
        RestoreSql.Scalar<int>(connection, "SELECT COUNT(*) FROM sys.tables WHERE name LIKE N'[_][_]DevRefreshKeep[_]%'");

    private static async Task<int[]> ReadObjects(SqlConnection connection) =>
        (await RestoreSql.Query(connection, """
            SELECT object_id, index_id FROM sys.indexes
            WHERE object_id IN (OBJECT_ID(N'dbo.Movements'),OBJECT_ID(N'dbo.Dimensions'),OBJECT_ID(N'dbo.Totals'))
            ORDER BY object_id,index_id
            """)).SelectMany(row => new[] { (int)row["object_id"]!, (int)row["index_id"]! }).ToArray();

    private static Task<ReductionResult> Apply(SqlSettings settings, RestoreSql sql, AnalysisData analysis, bool rewrite,
        Action<string>? progress = null, CancellationToken token = default, bool shrinkBuild = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Shrink:RewriteTables"] = rewrite.ToString(),
            ["SqlDiagnostics:Enabled"] = "false"
        }).Build();
        var executor = new ReductionExecutor(sql, NullLogger<ReductionExecutor>.Instance, config);
        return executor.Apply(settings, "DevRefreshStage_" + analysis.Id, analysis,
            [new("main","2025-01-01"), new("totals",null)], true, progress ?? (_ => { }), token,
            jobId: analysis.Id, cleanBinaryData: shrinkBuild);
    }

    private static Task SeedRows(SqlConnection connection) => RestoreSql.Execute(connection, """
        TRUNCATE TABLE dbo.Movements;
        TRUNCATE TABLE dbo.Dimensions;
        TRUNCATE TABLE dbo.Totals;
        ;WITH Numbers AS (
            SELECT TOP (300000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Id
            FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN (VALUES (1),(2),(3),(4)) c(n)
        )
        INSERT dbo.Movements(Id,_Period,Payload)
        SELECT Id, CASE WHEN Id<=240000 THEN '2024-12-31T23:59:59' ELSE '2025-01-01' END,
               CONVERT(binary(8),Id)
        FROM Numbers;
        INSERT dbo.Dimensions(Id,_Period,Payload)
        SELECT Id, CASE WHEN Id%20=0 THEN NULL ELSE _Period END, Payload
        FROM dbo.Movements;
        INSERT dbo.Totals (Id,Payload) SELECT Id,Payload FROM dbo.Movements;
        """);

    private static async Task InStage(Func<SqlSettings, RestoreSql, SqlConnection, AnalysisData, Task> test)
    {
        var id = Guid.NewGuid().ToString("N");
        var db = "DevRefreshStage_" + id;
        var settings = new SqlSettings(@"(localdb)\MSSQLLocalDB","windows","","","","",true);
        var sql = new RestoreSql(new SettingsService(null!,new EphemeralDataProtectionProvider()));
        await using var master = await sql.Open(settings);
        await RestoreSql.Execute(master, $"CREATE DATABASE {RestoreSql.Q(db)}");
        try
        {
            await using var connection = await sql.Open(settings, db);
            await RestoreSql.Execute(master, $"ALTER DATABASE {RestoreSql.Q(db)} SET RECOVERY SIMPLE");
            await RestoreSql.Execute(connection, $"""
                EXEC sys.sp_addextendedproperty @name=N'DevRefresh.Resource',@value={RestoreSql.L(id)};
                CREATE TABLE dbo.Movements (Id int NOT NULL PRIMARY KEY, _Period datetime2 NOT NULL, Payload binary(8) NOT NULL);
                CREATE INDEX IX_Period ON dbo.Movements(_Period,Id);
                CREATE TABLE dbo.Dimensions (Id int NOT NULL, _Period datetime2 NULL, Payload binary(8) NOT NULL);
                CREATE INDEX IX_Dimensions ON dbo.Dimensions(_Period,Id);
                CREATE TABLE dbo.Totals (Id int NOT NULL PRIMARY KEY, Payload binary(8) NOT NULL);
                """);
            var analysis = new AnalysisData(id,"test","test.bak","Test","1",0,"test",
                [Table("main","Movements","Main","main",period:true,nullable:false,keys:["Id"]),
                 Table("detail","Dimensions","DimValues","main",period:true,nullable:true,keys:[]),
                 Table("totals","Totals","Main","totals",period:false,nullable:false,keys:["Id"])], [], "");
            await test(settings,sql,connection,analysis);
        }
        finally
        {
            await sql.DropOwned(settings,db,id,CancellationToken.None,registered:true);
        }
    }

    private static AnalyzedTable Table(string key, string name, string role, string root,
        bool period, bool nullable, string[] keys)
    {
        var columns = new List<ColumnShape> { new("Id","int",4,false,false,false) };
        if (period) columns.Add(new("_Period","datetime2",8,nullable,false,false));
        columns.Add(new("Payload","binary",8,false,false,false));
        return new(key,key,"dbo",name,"РегистрБухгалтерии",root,role,1,role=="Main",null,
            period && role=="Main" ? "_Period" : null,null,null,0,[],[.. columns],keys,300_000,0,0,0,0);
    }
}
