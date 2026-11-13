using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace DevRefresh;

internal sealed class SqlReductionDiagnostics(
    RestoreSql sql, SqlSettings settings, SqlConnection target, ILogger logger, string jobId)
{
    private static readonly TimeSpan FirstSample = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, int> reportedPlans = new(StringComparer.Ordinal);
    private bool samplesUnavailable;
    private bool plansUnavailable;
    private long nextOperation;
    private readonly string database = target.Database;
    private readonly int session = target.ServerProcessId;

    public async Task<T> Run<T>(string operation, AnalyzedTable? table, Func<Task<T>> execute,
        CancellationToken token, long? batch = null, long? removed = null, DateTime? month = null, int? batchSize = null,
        string? physicalTable = null, bool logFast = true)
    {
        var sequence = ++nextOperation;
        var name = physicalTable ?? (table is null ? null : $"{table.Schema}.{table.Table}");
        var logStart = logFast && (batch is null or <= 3 || batch % 20 == 0);
        var before = batch is not null && logStart && !samplesUnavailable
            ? await ReadSessionCounters(token) : null;
        var clock = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sampling = samplesUnavailable ? Task.CompletedTask : SampleWhileRunning(sequence, operation, name, batch, month, stop.Token);
        if (logStart)
            logger.LogInformation(
                "SQL diagnostic started job {JobId}, operation {OperationId} {Operation}, database {Database}, SPID {SqlSessionId}, table {Table}, batch {Batch}, SQL month {SqlMonth}, rows before {RowsBefore}",
                jobId, sequence, operation, database, session, name, batch, month, removed);
        try
        {
            var result = await execute();
            var elapsed = clock.ElapsedMilliseconds;
            await stop.CancelAsync();
            await sampling;
            if (before is not null && await ReadSessionCounters(token) is { } after)
                logger.LogInformation(
                    "SQL diagnostic batch I/O job {JobId}, operation {OperationId}, table {Table}, SPID {SqlSessionId}, batch {Batch}: logical reads {LogicalReads}, physical reads {PhysicalReads}, writes {Writes}, CPU {CpuMs} ms",
                    jobId, sequence, name, session, batch, after.LogicalReads-before.LogicalReads,
                    after.PhysicalReads-before.PhysicalReads, after.Writes-before.Writes, after.CpuMs-before.CpuMs);
            if (logStart || clock.Elapsed >= FirstSample || result is long count && count < batchSize)
                logger.LogInformation(
                    "SQL diagnostic completed job {JobId}, operation {OperationId} {Operation}, database {Database}, SPID {SqlSessionId}, table {Table}, batch {Batch}, SQL month {SqlMonth}, duration {ElapsedMs} ms, rows before {RowsBefore}, affected rows {AffectedRows}",
                    jobId, sequence, operation, database, session, name, batch, month, elapsed, removed,
                    result is long rows ? rows : null);
            return result;
        }
        catch (Exception error)
        {
            logger.LogWarning(
                "SQL diagnostic failed job {JobId}, operation {OperationId} {Operation}, database {Database}, SPID {SqlSessionId}, table {Table}, batch {Batch}, SQL month {SqlMonth}, duration {ElapsedMs} ms, rows before {RowsBefore}, SQL error {SqlErrorNumber}, error type {ErrorType}",
                jobId, sequence, operation, database, session, name, batch, month, clock.ElapsedMilliseconds, removed,
                (error as SqlException)?.Number, error.GetType().Name);
            throw;
        }
        finally
        {
            await stop.CancelAsync();
            await sampling;
        }
    }

    private sealed record SessionCounters(long LogicalReads, long PhysicalReads, long Writes, int CpuMs);

    private async Task<SessionCounters?> ReadSessionCounters(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ProbeTimeout);
            await using var observer = await sql.Open(settings, token: timeout.Token);
            var rows = await RestoreSql.Query(observer, $"""
                SELECT logical_reads,reads,writes,cpu_time FROM sys.dm_exec_sessions WHERE session_id={session}
                """, timeout.Token, timeoutSeconds: 2);
            if (rows.SingleOrDefault() is not { } row)
            {
                DisableSamples("SQL Server не показывает счётчики другой сессии.", null);
                return null;
            }
            return new((long)row["logical_reads"]!, (long)row["reads"]!, (long)row["writes"]!, (int)row["cpu_time"]!);
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
                DisableSamples(error.GetType().Name, (error as SqlException)?.Number);
            return null;
        }
    }

    private async Task SampleWhileRunning(long sequence, string operation, string? table, long? batch, DateTime? month, CancellationToken token)
    {
        try
        {
            await Task.Delay(FirstSample, token);
            while (!token.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ProbeTimeout);
                await using (var observer = await sql.Open(settings, token: timeout.Token))
                {
                    var rows = await RestoreSql.Query(observer, $"""
                        SELECT r.status AS Status, r.command AS Command, r.wait_type AS WaitType,
                               r.last_wait_type AS LastWaitType, r.wait_time AS WaitMs,
                               r.blocking_session_id AS BlockingSessionId, r.cpu_time AS CpuMs,
                               r.total_elapsed_time AS SqlElapsedMs, r.logical_reads AS LogicalReads,
                               r.reads AS PhysicalReads, r.writes AS Writes, r.row_count AS SqlRowCount,
                               r.open_transaction_count AS OpenTransactions,
                               CONVERT(varchar(18),r.query_plan_hash,1) AS PlanHash,
                               tx.database_transaction_log_bytes_used AS TransactionLogBytesUsed,
                               tx.database_transaction_log_bytes_reserved AS TransactionLogBytesReserved,
                               d.log_reuse_wait_desc AS LogReuseWait
                        FROM sys.dm_exec_requests r
                        JOIN sys.databases d ON d.database_id=r.database_id
                        LEFT JOIN sys.dm_tran_database_transactions tx
                          ON tx.transaction_id=r.transaction_id AND tx.database_id=r.database_id
                        WHERE r.session_id={session}
                        """, timeout.Token, timeoutSeconds: 2);
                    if (rows.SingleOrDefault() is { } sample)
                    {
                        logger.LogInformation(
                            "SQL diagnostic sample job {JobId}, operation {OperationId} {Operation}, database {Database}, SPID {SqlSessionId}, table {Table}, batch {Batch}, SQL month {SqlMonth}: {@SqlSample}",
                            jobId, sequence, operation, database, session, table, batch, month, sample);
                        if (!plansUnavailable && sample["PlanHash"] is string hash && hash!="0x0000000000000000")
                        {
                            var count = reportedPlans.GetValueOrDefault(hash);
                            if ((count == 0 && reportedPlans.Count < 32) ||
                                (count == 1 && sample["SqlElapsedMs"] is int elapsed && elapsed >= 60_000))
                                await ReadPlan(observer, sequence, table, hash, count, timeout.Token);
                        }
                    }
                    else
                    {
                        var canView = await RestoreSql.Scalar<int>(observer, """
                            SELECT CASE WHEN HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER STATE')=1
                                          OR HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER PERFORMANCE STATE')=1
                                        THEN 1 ELSE 0 END
                            """, timeout.Token, timeoutSeconds: 2);
                        if (canView == 0)
                        {
                            DisableSamples("Недостаточно прав для просмотра другой SQL-сессии.", null);
                            return;
                        }
                    }
                }
                await Task.Delay(SampleInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SqlException) when (token.IsCancellationRequested) { }
        catch (SqlException error) when (error.Number is 229 or 297 or 300 or 262)
        {
            DisableSamples("Недостаточно прав для чтения SQL DMV.", error.Number);
        }
        catch (Exception error)
        {
            // Diagnostic failures must not affect the command or its transaction.
            DisableSamples(error.GetType().Name, (error as SqlException)?.Number);
        }
    }

    private void DisableSamples(string reason, int? sqlError)
    {
        if (samplesUnavailable) return;
        samplesUnavailable = true;
        logger.LogWarning(
            "SQL diagnostic sampling unavailable for job {JobId}, database {Database}, SPID {SqlSessionId}: {Reason}, SQL error {SqlErrorNumber}. Command timings remain available.",
            jobId, database, session, reason, sqlError);
    }

    private async Task ReadPlan(SqlConnection observer, long sequence, string? table, string hash, int priorSamples, CancellationToken token)
    {
        try
        {
            var xml = await RestoreSql.Scalar<object?>(observer, $"""
                SELECT COALESCE(CONVERT(nvarchar(max), live.query_plan), cached.query_plan)
                FROM sys.dm_exec_requests r
                OUTER APPLY sys.dm_exec_query_statistics_xml(r.session_id) live
                OUTER APPLY sys.dm_exec_text_query_plan(r.plan_handle,r.statement_start_offset,r.statement_end_offset) cached
                WHERE r.session_id={session} AND (live.request_id IS NULL OR live.request_id=r.request_id)
                  AND CONVERT(varchar(18),r.query_plan_hash,1)={RestoreSql.L(hash)}
                """, token, timeoutSeconds: 2) as string;
            if (string.IsNullOrWhiteSpace(xml) || xml.Length > 4_000_000) return;
            var operators = ReadOperators(xml);
            if (operators.Length == 0) return;
            reportedPlans[hash] = priorSamples + 1;
            logger.LogInformation(
                "SQL diagnostic plan job {JobId}, operation {OperationId}, table {Table}, SPID {SqlSessionId}, hash {PlanHash}: {@SqlOperators}",
                jobId, sequence, table, session, hash, operators);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            plansUnavailable = true;
            logger.LogWarning("SQL diagnostic plan unavailable for job {JobId}, SPID {SqlSessionId}: {ErrorType}, SQL error {SqlErrorNumber}",
                jobId, session, error.GetType().Name, (error as SqlException)?.Number);
        }
    }

    internal async Task RecordTable(AnalyzedTable table, bool useMonths, int batchSize, DateTime? cutoff, CancellationToken token)
    {
        logger.LogInformation(
            "SQL diagnostic auxiliary table job {JobId}, database {Database}, SPID {SqlSessionId}, table {Table}, object {LogicalObject}, estimated rows {EstimatedRows}, data MiB {DataMiB}, index MiB {IndexMiB}, month ranges {MonthRanges}, SQL cutoff {SqlCutoff}, batch size {BatchSize}, key columns {KeyColumns}",
            jobId, database, session, $"{table.Schema}.{table.Table}", table.LogicalName, table.Rows,
            table.DataMB, table.IndexMB, useMonths, cutoff, batchSize, string.Join(",",table.Keys));
        if (table.Rows <= batchSize && table.Role!="DimValues") return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ProbeTimeout);
            await using var observer = await sql.Open(settings,database,timeout.Token);
            var indexes = await RestoreSql.Query(observer,$"""
                SELECT TOP (128) i.name AS IndexName, i.type_desc AS IndexType, i.is_unique AS IsUnique,
                       i.has_filter AS IsFiltered, ic.key_ordinal AS KeyOrdinal, c.name AS ColumnName,
                       ic.is_descending_key AS IsDescending, ic.is_included_column AS IsIncluded
                FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
                JOIN sys.indexes i ON i.object_id=t.object_id
                LEFT JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                LEFT JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE s.name={RestoreSql.L(table.Schema)} AND t.name={RestoreSql.L(table.Table)}
                  AND i.is_disabled=0 AND i.is_hypothetical=0
                ORDER BY i.index_id, ic.key_ordinal, ic.index_column_id
                """,timeout.Token,timeoutSeconds:2);
            logger.LogInformation("SQL diagnostic indexes job {JobId}, database {Database}, table {Table}: {@SqlIndexes}",
                jobId,database,$"{table.Schema}.{table.Table}",indexes);
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
                logger.LogWarning("SQL diagnostic indexes unavailable for job {JobId}, table {Table}: {ErrorType}, SQL error {SqlErrorNumber}",
                    jobId,$"{table.Schema}.{table.Table}",error.GetType().Name,(error as SqlException)?.Number);
        }
    }

    internal void RecordTableComplete(AnalyzedTable table, long batches, long rows) =>
        logger.LogInformation(
            "SQL diagnostic auxiliary table complete job {JobId}, database {Database}, SPID {SqlSessionId}, table {Table}, batches {BatchCount}, rows {AffectedRows}",
            jobId, database, session, $"{table.Schema}.{table.Table}", batches, rows);

    internal static object[] ReadOperators(string xml)
    {
        var plan = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        static long? SumCounter(XElement[] counters, string name)
        {
            var values = counters.Attributes(name).Select(value => (long)value).ToArray();
            return values.Length == 0 ? null : values.Sum();
        }
        return plan.Descendants(ns+"RelOp").Take(100).Select(op =>
        {
            var source = op.Elements().SelectMany(element => element.Elements(ns+"Object")).FirstOrDefault();
            var counters = op.Elements(ns+"RunTimeInformation").Elements(ns+"RunTimeCountersPerThread").ToArray();
            return (object)new
            {
                Node = (string?)op.Attribute("NodeId"), Physical = (string?)op.Attribute("PhysicalOp"),
                Logical = (string?)op.Attribute("LogicalOp"), EstimatedRows = (string?)op.Attribute("EstimateRows"),
                Table = (string?)source?.Attribute("Table"), Index = (string?)source?.Attribute("Index"),
                ActualRows = SumCounter(counters, "ActualRows"), ActualRowsRead = SumCounter(counters, "ActualRowsRead"),
                LogicalReads = SumCounter(counters, "ActualLogicalReads"), Executions = SumCounter(counters, "ActualExecutions")
            };
        }).ToArray();
    }
}
