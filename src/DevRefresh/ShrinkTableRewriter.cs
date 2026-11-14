using Microsoft.Data.SqlClient;

namespace DevRefresh;

internal sealed class ShrinkTableRewriter(
    SqlConnection connection, SqlTransaction transaction, SqlReductionDiagnostics? diagnostics,
    ILogger logger, Action<string> progress, int minimumDeletePercent)
{
    private const long MinimumRows = 250_000;

    internal static bool IsLargeEnough(AnalyzedTable table) => table.Rows >= MinimumRows;

    internal async Task<long?> TryRewrite(AnalyzedTable table, string keepPredicate,
        CancellationToken token, string? deleteKeyTable = null)
    {
        if (!await SupportsRewrite(table, token))
            return null;

        var source = RestoreSql.Q(table.Schema) + "." + RestoreSql.Q(table.Table);
        async Task<T> Observe<T>(string operation, Func<Task<T>> execute) =>
            diagnostics is null ? await execute() : await diagnostics.Run(operation, table, execute, token);

        Task<long> CountedCommand(string operation, string statement) =>
            Observe(operation, () => RestoreSql.Scalar<long>(connection,
                statement + "; SELECT ROWCOUNT_BIG()", token, transaction));

        async Task Execute(string operation, string statement)
        {
            await Observe(operation, async () =>
            {
                await RestoreSql.Execute(connection, statement, token, transaction);
                return true;
            });
        }

        // COUNT_BIG over a projected flag also works for keep predicates containing NOT EXISTS.
        var volume = await Observe("rewrite-count-rows", async () =>
        {
            if (deleteKeyTable is not null)
            {
                // The unique key set was built earlier in this session against the private staging database.
                var counts = await RestoreSql.Query(connection, $"""
                    SELECT (SELECT COUNT_BIG(*) FROM {source} WITH (TABLOCK)) AS TotalRows,
                           (SELECT COUNT_BIG(*) FROM {deleteKeyTable}) AS RemovedRows
                    """, token, transaction);
                var totalRows = (long)counts[0]["TotalRows"]!;
                return (Total: totalRows, Kept: totalRows - (long)counts[0]["RemovedRows"]!);
            }
            var countsByPredicate = await RestoreSql.Query(connection, $"""
                SELECT COUNT_BIG(*) AS TotalRows, COUNT_BIG(KeepRow) AS KeptRows
                FROM (
                    SELECT CASE WHEN ({keepPredicate}) THEN 1 END AS KeepRow
                    FROM {source} s WITH (TABLOCK)
                ) AS classified
                """, token, transaction);
            return (Total: (long)countsByPredicate[0]["TotalRows"]!, Kept: (long)countsByPredicate[0]["KeptRows"]!);
        });
        var (total, kept) = volume;
        if (kept < 0)
            throw new InputException($"Число ключей к удалению превышает число строк таблицы {table.LogicalName}.");
        var removed = total - kept;
        if (removed == 0)
            return 0;
        if (total < MinimumRows || removed * 100m < total * (decimal)minimumDeletePercent)
            return null;

        logger.LogInformation(
            "Shrink table rewrite on {Database}, table {Table}: total {TotalRows}, keep {KeptRows}, remove {RemovedRows}",
            connection.Database, source, total, kept, removed);

        if (kept == 0)
        {
            progress($"Очистка таблицы · {table.LogicalName}");
            await Execute("rewrite-truncate-table", $"TRUNCATE TABLE {source}");
            return removed;
        }

        var workTable = "[dbo]." + RestoreSql.Q("__DevRefreshKeep_" + Guid.NewGuid().ToString("N"));
        var columns = string.Join(",", table.Columns.Select(column => RestoreSql.Q(column.Name)));
        progress($"Сохранение оставляемых строк · {table.LogicalName}");
        var copied = await CountedCommand("rewrite-copy-kept-rows", $"""
            SELECT {columns} INTO {workTable} FROM {source} s WITH (TABLOCK)
            WHERE ({keepPredicate})
            """);
        if (copied != kept)
            throw new InputException($"Число сохранённых строк изменилось для {table.LogicalName}.");

        // The original table and the working copy share the table's transaction.
        await Execute("rewrite-truncate-table", $"TRUNCATE TABLE {source}");
        progress($"Возврат сохранённых строк · {table.LogicalName} · {kept:N0} строк");
        token.ThrowIfCancellationRequested();
        var restored = await CountedCommand("rewrite-restore-kept-rows", $"""
            INSERT INTO {source} WITH (TABLOCK) ({columns})
            SELECT {columns} FROM {workTable}
            """);
        if (restored != kept)
            throw new InputException($"В таблицу {table.LogicalName} возвращено не ожидаемое число строк.");
        await Execute("rewrite-drop-work-table", $"DROP TABLE {workTable}");
        return removed;
    }

    private Task<bool> SupportsRewrite(AnalyzedTable table, CancellationToken token) =>
        RestoreSql.Scalar<bool>(connection, $"""
            SELECT CONVERT(bit, CASE WHEN EXISTS (
                SELECT 1 FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id=t.schema_id
                WHERE s.name={RestoreSql.L(table.Schema)} AND t.name={RestoreSql.L(table.Table)}
                  AND t.is_ms_shipped=0 AND t.is_memory_optimized=0 AND t.temporal_type=0
                  AND t.is_filetable=0 AND t.is_replicated=0 AND t.is_merge_published=0
                  AND t.is_sync_tran_subscribed=0 AND t.is_tracked_by_cdc=0
                  AND HAS_PERMS_BY_NAME(QUOTENAME(s.name)+N'.'+QUOTENAME(t.name),N'OBJECT',N'ALTER')=1
                  AND NOT EXISTS (SELECT 1 FROM sys.change_tracking_tables ct WHERE ct.object_id=t.object_id)
                  AND NOT EXISTS (
                    SELECT 1 FROM sys.check_constraints ck
                    WHERE ck.parent_object_id=t.object_id AND ck.is_disabled=0 AND ck.is_not_trusted=1
                  )
                  AND NOT EXISTS (
                    SELECT 1 FROM sys.columns c WHERE c.object_id=t.object_id
                      AND (c.is_identity=1 OR c.is_computed=1 OR c.system_type_id IN (189,240)
                           OR c.is_hidden=1 OR c.generated_always_type<>0 OR c.is_filestream=1
                           OR c.is_masked=1 OR c.encryption_type IS NOT NULL OR c.is_sparse=1 OR c.is_column_set=1)
                  )
                  AND NOT EXISTS (
                    SELECT 1 FROM sys.security_predicates p WHERE p.target_object_id=t.object_id
                  )
                  AND NOT EXISTS (
                    SELECT 1 FROM sys.sql_expression_dependencies d
                    WHERE d.referenced_id=t.object_id AND d.referencing_id<>t.object_id
                      AND d.is_schema_bound_reference=1
                  )
            ) THEN 1 ELSE 0 END)
            """, token, transaction);
}
