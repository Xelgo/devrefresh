using Microsoft.Data.SqlClient;

namespace DevRefresh;

internal sealed class BinaryDataCleanup
{
    private const int BatchSize = 2_000;
    private const string Target = "[dbo].[BinaryData]";
    private const string Marker = "0x53544F52484452";
    private readonly SqlConnection connection;
    private readonly (string Schema, string Table, string[] Columns)[] tables;

    private BinaryDataCleanup(SqlConnection connection, string keyType,
        (string Schema, string Table, string[] Columns)[] tables)
    {
        this.connection = connection;
        this.tables = tables;
        KeyType = keyType;
    }

    private string KeyType { get; }

    internal static async Task<BinaryDataCleanup?> Start(SqlConnection connection, string stageId, CancellationToken token)
    {
        // The restored staging database is private to the build, so no application writers
        // can introduce a reference between the scan and the batched deletes.
        var owner = await RestoreSql.Scalar<object?>(connection, """
            SELECT CONVERT(nvarchar(64),value) FROM sys.extended_properties
            WHERE class=0 AND name=N'DevRefresh.Resource'
            """,token);
        if (owner is not string marker || marker != stageId)
            throw new InputException("Не удалось подтвердить владельца временной базы для очистки BinaryData.");

        var layout = await RestoreSql.Query(connection, """
            SELECT name, system_type_id, max_length, is_nullable
            FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.BinaryData',N'U')
            """,token);
        if (layout.Count == 0) return null;

        var key = layout.Find(column => Equals(column["name"],"f_key"));
        var data = layout.Find(column => Equals(column["name"],"f_data"));
        if (key is null || data is null || key["system_type_id"] is not ((byte)165 or (byte)173) ||
            !Equals(key["max_length"],(short)16) || !Equals(key["is_nullable"],false) ||
            data["system_type_id"] is not ((byte)34 or (byte)165))
            throw new InputException("Структура dbo.BinaryData не поддерживается. Проверьте столбцы f_key и f_data.");
        if (await RestoreSql.Scalar<int>(connection,
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.BinaryData) THEN 1 ELSE 0 END",token)==0) return null;

        var leadingIndex = await RestoreSql.Scalar<int>(connection, """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE i.object_id=OBJECT_ID(N'dbo.BinaryData',N'U') AND ic.key_ordinal=1 AND c.name=N'f_key'
                  AND i.type IN (1,2) AND i.is_disabled=0 AND i.is_hypothetical=0 AND i.has_filter=0
            ) THEN 1 ELSE 0 END
            """,token);
        if (leadingIndex == 0)
            throw new InputException("У dbo.BinaryData нет индекса с первым столбцом f_key для удаления порциями.");

        var inventory = await RestoreSql.Query(connection, """
            SELECT s.name AS SchemaName,t.name AS TableName,c.name AS ColumnName
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.columns c ON c.object_id=t.object_id
            WHERE t.is_ms_shipped=0 AND t.object_id<>OBJECT_ID(N'dbo.BinaryData',N'U')
              AND (c.system_type_id=34 OR (c.system_type_id IN (165,173) AND (c.max_length=-1 OR c.max_length>=32)))
            ORDER BY s.name,t.name,c.column_id
            """,token);
        var tables = inventory.GroupBy(row=>((string)row["SchemaName"]!,(string)row["TableName"]!))
            .Select(group=>(group.Key.Item1,group.Key.Item2,group.Select(row=>(string)row["ColumnName"]!).ToArray()))
            .ToArray();
        var keyType = Equals(key["system_type_id"],(byte)173) ? "binary(16)" : "varbinary(16)";
        await RestoreSql.Execute(connection,$"CREATE TABLE #ShrinkBinaryCandidates (BlobKey {keyType} NOT NULL PRIMARY KEY)",token);
        return new(connection,keyType,tables);
    }

    private static string Table(string schema,string table) => $"{RestoreSql.Q(schema)}.{RestoreSql.Q(table)}";

    private string ExtractKeys(string[] columns)
    {
        var values = columns.Select(column =>
        {
            var value = $"s.{RestoreSql.Q(column)}";
            return $"(CASE WHEN DATALENGTH({value})>=32 AND SUBSTRING({value},1,7)={Marker} " +
                $"THEN CONVERT({KeyType},SUBSTRING({value},17,16)) END)";
        });
        return $"CROSS APPLY (VALUES {string.Join(",",values)}) k(BlobKey)";
    }

    internal string? CaptureSql(AnalyzedTable table, string join, string predicate)
    {
        var columns = tables.FirstOrDefault(item=>item.Schema==table.Schema && item.Table==table.Table).Columns;
        if (columns is not { Length: > 0 }) return null;
        return $"""
            INSERT #ShrinkBinaryCandidates(BlobKey)
            SELECT DISTINCT k.BlobKey FROM {Table(table.Schema,table.Table)} s WITH (TABLOCK) {join}
            {ExtractKeys(columns)}
            WHERE ({predicate}) AND k.BlobKey IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM #ShrinkBinaryCandidates known WHERE known.BlobKey=k.BlobKey
            )
            """;
    }

    internal async Task<BinaryDataCleanupReport> RemoveUnreferenced(Action<string> progress, Action<string> liveProgress,
        SqlReductionDiagnostics? diagnostics, ILogger logger, string jobId, CancellationToken token)
    {
        const string countCandidates = "SELECT COUNT_BIG(*) FROM #ShrinkBinaryCandidates";
        var candidates = await RestoreSql.Scalar<long>(connection,countCandidates,token);
        if (candidates == 0) return new(0,0,0,0,0);

        const string checkStage = "Проверка оставшихся ссылок BinaryData";
        progress(checkStage);
        for (var i=0; i<tables.Length; i++)
        {
            var (schema,table,columns) = tables[i];
            var name = Table(schema,table);
            if (i%25==0) liveProgress($"{checkStage} · {i+1}/{tables.Length} · {name}");
            var statement = $"""
                DELETE candidate FROM #ShrinkBinaryCandidates candidate
                JOIN (
                    SELECT DISTINCT k.BlobKey FROM {name} s WITH (TABLOCK)
                    {ExtractKeys(columns)} WHERE k.BlobKey IS NOT NULL
                ) surviving ON surviving.BlobKey=candidate.BlobKey;
                SELECT ROWCOUNT_BIG();
                """;
            Task<long> Check() => RestoreSql.Scalar<long>(connection,statement,token);
            var retained = diagnostics is null ? await Check()
                : await diagnostics.Run("binarydata-check-references",null,Check,token,physicalTable:name,logFast:false);
            if (retained > 0)
            {
                logger.LogInformation("Shrink BinaryData {JobId}: {Table} still refers to {RetainedKeys} candidates",jobId,name,retained);
                if (!await RestoreSql.Scalar<bool>(connection,
                    "SELECT CONVERT(bit,CASE WHEN EXISTS (SELECT 1 FROM #ShrinkBinaryCandidates) THEN 1 ELSE 0 END)",token))
                    return new(candidates,candidates,0,0,0);
            }
        }
        var remaining = await RestoreSql.Scalar<long>(connection,countCandidates,token);
        progress("Удаление блоков BinaryData");
        await RestoreSql.Execute(connection,$"""
            CREATE TABLE #ShrinkBinaryBatch (BlobKey {KeyType} NOT NULL PRIMARY KEY);
            CREATE TABLE #ShrinkBinaryDeleted (BlobKey {KeyType} NOT NULL, Bytes bigint NOT NULL);
            """,token);
        long keys = 0, blocks = 0, bytes = 0, batch = 0;
        var lastProgress = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var statement = $"""
                TRUNCATE TABLE #ShrinkBinaryBatch;
                TRUNCATE TABLE #ShrinkBinaryDeleted;
                INSERT #ShrinkBinaryBatch SELECT TOP ({BatchSize}) BlobKey FROM #ShrinkBinaryCandidates ORDER BY BlobKey;
                DECLARE @batchKeys int=@@ROWCOUNT;
                DELETE blob
                OUTPUT deleted.f_key, COALESCE(CONVERT(bigint,DATALENGTH(deleted.f_data)),0)
                    INTO #ShrinkBinaryDeleted(BlobKey,Bytes)
                FROM #ShrinkBinaryBatch batch
                INNER LOOP JOIN {Target} blob ON blob.f_key=batch.BlobKey;
                DELETE candidate FROM #ShrinkBinaryCandidates candidate
                    JOIN #ShrinkBinaryBatch batch ON batch.BlobKey=candidate.BlobKey;
                SELECT @batchKeys AS BatchKeys, COUNT_BIG(DISTINCT BlobKey) AS DeletedKeys,
                    COUNT_BIG(*) AS Blocks, COALESCE(SUM(Bytes),0) AS Bytes FROM #ShrinkBinaryDeleted;
                """;
            Task<List<Dictionary<string,object?>>> Delete() => RestoreSql.Query(connection,statement,token);
            var result = diagnostics is null ? await Delete()
                : await diagnostics.Run("binarydata-delete-blocks",null,Delete,token,batch:++batch,
                    removed:blocks,physicalTable:Target);
            var row = result.Single();
            keys += (long)row["DeletedKeys"]!;
            blocks += (long)row["Blocks"]!;
            bytes += (long)row["Bytes"]!;
            if (lastProgress.Elapsed >= TimeSpan.FromSeconds(10))
            {
                liveProgress($"Удаление блоков BinaryData · {blocks:N0} блоков");
                lastProgress.Restart();
            }
            if ((int)row["BatchKeys"]! < BatchSize) break;
        }
        return new(candidates,candidates-remaining,keys,blocks,bytes);
    }
}
