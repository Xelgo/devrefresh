using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace DevRefresh;

public sealed class ReductionExecutor(RestoreSql sql, ILogger<ReductionExecutor> logger, IConfiguration configuration)
{
    private sealed record DeleteSet(AnalyzedTable Table,string Name);
    private sealed record FilteredDelete(AnalyzedTable Table,string Predicate,bool ByPeriod=false,DateTime? Cutoff=null,bool FullTable=false);
    private const int LegacyDeleteBatchSize = 250_000;
    private const int ShrinkDeleteBatchSize = 50_000;
    private static string Q(string s)=>RestoreSql.Q(s);
    private static string Table(AnalyzedTable t)=>Q(t.Schema)+"."+Q(t.Table);
    private static string KeyJoin(AnalyzedTable t,string left,string right)=>string.Join(" AND ",t.Keys.Select(k=>$"({left}.{Q(k)}={right}.{Q(k)} OR ({left}.{Q(k)} IS NULL AND {right}.{Q(k)} IS NULL))"));
    private static string Scope(AnalyzedTable source,AnalyzedTable target,string s,string p)=>string.Concat(target.Fields.Where(f=>f.Purpose.Contains("UseDataSeparation",StringComparison.Ordinal))
        .Where(f=>source.Columns.Any(c=>c.Name.Equals(f.Column,StringComparison.OrdinalIgnoreCase))).Select(f=>$" AND {s}.{Q(f.Column)}={p}.{Q(f.Column)}"));
    private static string Id(AnalyzedTable t)=>t.Fields.FirstOrDefault(f=>f.Name=="Ссылка" && f.Column=="_IDRRef")?.Column??"_IDRRef";
    private static bool Master(AnalyzedTable t)=>t.Role=="Main" && t.Kind is "Документ" or "Справочник" && t.Columns.Any(c=>c.Name=="_IDRRef");
    private static bool IsRegister(AnalyzedTable table) => table.Kind.StartsWith("Регистр", StringComparison.Ordinal);
    private static string PeriodLiteral(DateTime cutoff) =>
        RestoreSql.L(cutoff.ToString("yyyy-MM-ddTHH:mm:ss.fffffff",System.Globalization.CultureInfo.InvariantCulture));

    private static string KeepAuxiliaryRows(FilteredDelete deletion)
    {
        if (deletion.FullTable) return "1=0";
        if (deletion.Cutoff is { } cutoff)
        {
            var keep = $"s.[_Period]>{PeriodLiteral(cutoff)}";
            return deletion.Table.Columns.Single(c=>c.Name=="_Period").Nullable
                ? $"{keep} OR s.[_Period] IS NULL" : keep;
        }
        // WHERE only deletes TRUE. Rows evaluating to UNKNOWN must also survive.
        return $"CASE WHEN ({deletion.Predicate}) THEN 0 ELSE 1 END=1";
    }

    private static async Task<long> DeleteAuxiliary(
        SqlConnection connection,SqlTransaction? transaction,FilteredDelete deletion,Action<string> progress,
        SqlReductionDiagnostics? diagnostics,CancellationToken token)
    {
        var table = deletion.Table;
        var batchSize = transaction is null ? ShrinkDeleteBatchSize : LegacyDeleteBatchSize;
        var where = $"FROM {Table(table)} s WHERE ({deletion.Predicate})";
        var deleteFrom = transaction is null
            ? $"FROM {Table(table)} s WITH (TABLOCKX) WHERE ({deletion.Predicate})" : where;
        var stage = $"Удаление связанных строк · {table.LogicalName}";
        progress(stage);
        var lastProgress = Stopwatch.StartNew();
        long total = 0;
        long batch = 0;

        async Task DeleteBatches(string filter,DateTime? from=null,DateTime? until=null,object? scope=null)
        {
            await using var command = new SqlCommand(
                $"DELETE TOP ({batchSize}) s {deleteFrom}{filter}; SELECT ROWCOUNT_BIG()",connection,transaction)
                {CommandTimeout=RestoreSql.LongCommandTimeoutSeconds};
            if (from is not null) command.Parameters.Add("@periodFrom",SqlDbType.DateTime2).Value = from.Value;
            if (until is not null) command.Parameters.Add("@periodUntil",SqlDbType.DateTime2).Value = until.Value;
            if (scope is not null) command.Parameters.AddWithValue("@scope",scope);
            long rows;
            do
            {
                token.ThrowIfCancellationRequested();
                async Task<long> ExecuteBatch() => (long)(await command.ExecuteScalarAsync(token))!;
                rows = diagnostics is null ? await ExecuteBatch()
                    : await diagnostics.Run("delete-auxiliary-batch",table,ExecuteBatch,token,++batch,total,from,batchSize);
                total += rows;
                if ((transaction is null && rows > 0 && total == rows) || lastProgress.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    progress($"{stage} · {total:N0} строк");
                    lastProgress.Restart();
                }
            } while (rows == batchSize);
        }

        // For an indexed period, move forward through disjoint ranges to avoid revisiting earlier pages.
        // In 1C the leading index column can be the data separation field.
        var periodPosition = Array.IndexOf(table.Keys,"_Period");
        var scopeColumn = periodPosition == 1
            && table.Fields.Any(f=>f.Column==table.Keys[0] && f.Purpose.Contains("UseDataSeparation",StringComparison.Ordinal))
            && table.Columns.Any(c=>c.Name==table.Keys[0] && !c.Nullable)
            ? $"s.{Q(table.Keys[0])}" : null;
        var usePeriodRanges = deletion.ByPeriod && table.Rows > batchSize
            && (periodPosition == 0 || scopeColumn is not null)
            && table.Columns.Any(c=>c.Name=="_Period" && c.SqlType=="datetime2" && !c.Nullable);
        if (diagnostics is not null)
            await diagnostics.RecordTable(table,usePeriodRanges,batchSize,deletion.Cutoff,token);
        if (!usePeriodRanges)
        {
            await DeleteBatches("");
            diagnostics?.RecordTableComplete(table,batch,total);
            return total;
        }

        var scopeSelect = scopeColumn is null ? "" : $",{scopeColumn} AS ScopeValue";
        var order = scopeColumn is null ? "PeriodMonth" : "ScopeValue,PeriodMonth";
        // Enumerate only occupied months because period values may include widely separated sentinel dates.
        Task<List<Dictionary<string,object?>>> ReadRanges() => RestoreSql.Query(connection,
            $"SELECT DISTINCT DATEFROMPARTS(YEAR(s.[_Period]),MONTH(s.[_Period]),1) AS PeriodMonth{scopeSelect} {where} ORDER BY {order}",
            token,transaction,RestoreSql.LongCommandTimeoutSeconds);
        var ranges = diagnostics is null ? await ReadRanges()
            : await diagnostics.Run("find-delete-periods",table,ReadRanges,token);
        foreach (var range in ranges)
        {
            var month = (DateTime)range["PeriodMonth"]!;
            var scopeValue = range.GetValueOrDefault("ScopeValue");
            var scopeFilter = scopeColumn is null ? "" : $" AND {scopeColumn}=@scope";
            if (month.Year==9999 && month.Month==12)
            {
                await DeleteBatches($"{scopeFilter} AND s.[_Period]>=@periodFrom",month,scope:scopeValue);
                continue;
            }
            await DeleteBatches($"{scopeFilter} AND s.[_Period]>=@periodFrom AND s.[_Period]<@periodUntil",
                month,month.AddMonths(1),scopeValue);
        }
        diagnostics?.RecordTableComplete(table,batch,total);
        return total;
    }

    public async Task<ReductionResult> Apply(SqlSettings settings,string database,AnalysisData analysis,ReductionRule[] selected,
        bool shrink,Action<string> progress,CancellationToken token,bool rollbackOnly=false,bool previewOnly=false,
        Action<string>? liveProgress=null,Func<string,AnalyzedTable,IDisposable>? measureTable=null,Action<string>? recordStage=null,
        string? jobId=null,bool cleanBinaryData=false)
    {
        progress("Проверка условий сокращения");
        if(Rules.ParseDatabase(database) is null && database!="DevRefreshStage_"+analysis.Id)
            throw new InputException("Удаление разрешено только в Dev-базе либо служебной базе текущего анализа.");
        if (cleanBinaryData && (!shrink || rollbackOnly || previewOnly || jobId!=analysis.Id ||
            !Guid.TryParseExact(analysis.Id,"N",out _) || database!="DevRefreshStage_"+analysis.Id))
            throw new InputException("Очистка BinaryData доступна только во временной базе сборки Shrink.");
        WorkflowRules.ValidateSelection(analysis,selected);
        await using var cn=await sql.Open(settings,database,token);
        if(await RestoreSql.Scalar<int>(cn,"SELECT COUNT(*) FROM sys.triggers WHERE parent_class=1 AND is_disabled=0",token)>0 || await RestoreSql.Scalar<int>(cn,"SELECT COUNT(*) FROM sys.foreign_keys",token)>0)
            throw new InputException("В базе есть SQL-триггеры или внешние ключи: автоматическое сокращение требует отдельной проверки.");
        var isolatedShrink = cleanBinaryData && selected.Length>0;
        // Start verifies that this database belongs to the Shrink build, even when BinaryData is absent.
        var binaryCleanup = isolatedShrink ? await BinaryDataCleanup.Start(cn,analysis.Id,token) : null;
        var rewriteTables = isolatedShrink && configuration.GetValue("Shrink:RewriteTables",true);
        var rewriteMinDeletePercent = rewriteTables
            ? Math.Clamp(configuration.GetValue("Shrink:RewriteMinDeletePercent",70),51,100) : 70;
        if (isolatedShrink)
            await RestoreSql.Execute(cn,"SET IMPLICIT_TRANSACTIONS OFF; SET TRANSACTION ISOLATION LEVEL READ COMMITTED",token);
        var all=analysis.Tables;var lookup=all.ToDictionary(x=>x.Key);var sets=new Dictionary<string,DeleteSet>(StringComparer.Ordinal);
        var notes=new List<string>();var counts=new List<DeletionCount>();
        SqlTransaction? tx = isolatedShrink ? null : (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable,token);
        var diagnostics = jobId is not null && configuration.GetValue("SqlDiagnostics:Enabled",true)
            ? new SqlReductionDiagnostics(sql,settings,cn,logger,jobId) : null;
        var reportProgress = liveProgress ?? progress;
        (string Action, AnalyzedTable Table)? currentOperation = null;
        var rollbackStarted = false;
        var reductionFailed = false;
        var committed = false;
        BinaryDataCleanupReport? binaryReport = null;

        async Task<T> Observe<T>(string operation, AnalyzedTable? table, Func<Task<T>> execute, CancellationToken cancellation)
            => diagnostics is null ? await execute() : await diagnostics.Run(operation,table,execute,cancellation);

        async Task Execute(string operation, AnalyzedTable? table, string command, CancellationToken cancellation)
        {
            await Observe(operation,table,async () =>
            {
                await RestoreSql.Execute(cn,command,cancellation,tx);
                return true;
            },cancellation);
        }

        async Task CaptureBinaryKeys(AnalyzedTable table, string join, string predicate)
        {
            if (binaryCleanup?.CaptureSql(table,join,predicate) is not { } statement) return;
            await Execute("binarydata-capture-keys",table,statement,token);
        }

        async Task Rollback()
        {
            rollbackStarted = true;
            if (tx?.Connection is not null)
            {
                recordStage?.Invoke(previewOnly ? "Завершение транзакции проверки плана" : "Откат сокращения");
                // SqlTransaction.Rollback uses the connection timeout. Use the long command timeout instead.
                await Execute("rollback",null,"IF XACT_STATE() <> 0 ROLLBACK TRANSACTION",CancellationToken.None);
            }
        }

        async Task<T> InShrinkTransaction<T>(AnalyzedTable table, Func<SqlTransaction,Task<T>> work)
        {
            var local = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted,token);
            var failed = false;
            try
            {
                var result = await work(local);
                token.ThrowIfCancellationRequested();
                await RestoreSql.Execute(cn,"COMMIT TRANSACTION",token,local);
                return result;
            }
            catch
            {
                failed = true;
                if (local.Connection is not null)
                {
                    try
                    {
                        await RestoreSql.Execute(cn,"IF XACT_STATE() <> 0 ROLLBACK TRANSACTION",CancellationToken.None,local);
                    }
                    catch (Exception error)
                    {
                        logger.LogWarning(error,"Не удалось подтвердить откат операции Shrink для {Database}, {Table}",
                            database,Table(table));
                    }
                }
                throw;
            }
            finally
            {
                try { local.Dispose(); }
                catch (Exception error) when (failed)
                {
                    logger.LogWarning(error,"Ошибка освобождения транзакции Shrink для {Database}, {Table}",
                        database,Table(table));
                }
            }
        }

        Task<long?> TryRewrite(AnalyzedTable table, string keep, string? deleteKeys = null) =>
            rewriteTables && ShrinkTableRewriter.IsLargeEnough(table)
                ? InShrinkTransaction(table, local =>
                    new ShrinkTableRewriter(cn,local,diagnostics,logger,reportProgress,rewriteMinDeletePercent)
                        .TryRewrite(table,keep,token,deleteKeys))
                : Task.FromResult<long?>(null);

        async Task<long> DeleteSelectedInBatches(DeleteSet set, string keyMatch)
        {
            var table = set.Table;
            var keys = string.Join(",",table.Keys.Select(Q));
            var emittedKeys = string.Join(",",table.Keys.Select(key=>"deleted."+Q(key)));
            var keyBatch = set.Name+"Batch";
            await RestoreSql.Execute(cn,$"""
                SELECT TOP(0) {keys} INTO {keyBatch} FROM {set.Name};
                CREATE UNIQUE CLUSTERED INDEX ix ON {keyBatch}({keys});
                """,token);

            var stage = $"Удаление выбранных строк · {table.LogicalName}";
            reportProgress(stage);
            var lastProgress = Stopwatch.StartNew();
            long total = 0;
            long batch = 0;
            long rows;
            do
            {
                token.ThrowIfCancellationRequested();
                Task<long> RunBatch() => InShrinkTransaction(table, async local =>
                {
                    var result = (await RestoreSql.Query(cn,$"""
                        TRUNCATE TABLE {keyBatch};
                        ;WITH BatchKeys AS (
                            SELECT TOP ({ShrinkDeleteBatchSize}) {keys} FROM {set.Name} ORDER BY {keys}
                        )
                        DELETE FROM BatchKeys OUTPUT {emittedKeys} INTO {keyBatch} ({keys});
                        DECLARE @picked bigint=ROWCOUNT_BIG();
                        DELETE s FROM {Table(table)} s WITH (TABLOCKX)
                        JOIN {keyBatch} d ON {keyMatch};
                        SELECT @picked AS Picked, ROWCOUNT_BIG() AS Removed;
                        """,token,local)).Single();
                    var removed = (long)result["Removed"]!;
                    if (removed != (long)result["Picked"]!)
                        throw new InputException($"Число удалённых строк не совпало со списком ключей для {table.LogicalName}.");
                    return removed;
                });
                rows = diagnostics is null ? await RunBatch()
                    : await diagnostics.Run("delete-selected-batch",table,RunBatch,token,++batch,total,batchSize:ShrinkDeleteBatchSize);
                total += rows;
                if ((rows > 0 && total == rows) || lastProgress.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    reportProgress($"{stage} · {total:N0} строк");
                    lastProgress.Restart();
                }
            } while (rows == ShrinkDeleteBatchSize);

            await RestoreSql.Execute(cn,$"DROP TABLE {keyBatch}",token);
            return total;
        }

        try
        {
            async Task<DeleteSet> Ensure(AnalyzedTable table)
            {
                if(sets.TryGetValue(table.Key,out var found))return found;
                if(table.Keys.Length==0)throw new InputException($"Для связанной таблицы {table.LogicalName} не найден однозначный ключ.");
                var result=new DeleteSet(table,"#Removed"+sets.Count);sets.Add(table.Key,result);
                await RestoreSql.Execute(cn,$"SELECT TOP(0) {string.Join(",",table.Keys.Select(Q))} INTO {result.Name} FROM {Table(table)}; CREATE UNIQUE CLUSTERED INDEX ix ON {result.Name}({string.Join(",",table.Keys.Select(Q))})",token,tx);
                return result;
            }
            async Task Add(AnalyzedTable table,string from,string predicate)
            {
                currentOperation = ("подбор строк к удалению", table);
                using var measured = measureTable?.Invoke("Подбор строк к удалению",table);
                var set=await Ensure(table);
                var text=$"INSERT INTO {set.Name} SELECT DISTINCT {string.Join(",",table.Keys.Select(k=>"s."+Q(k)))} FROM {Table(table)} s {from} WHERE ({predicate}) AND NOT EXISTS(SELECT 1 FROM {set.Name} d WHERE {KeyJoin(table,"s","d")})";
                await Execute("select-delete-keys",table,text,token);
            }
            var rules=(shrink?selected:[]).ToList();
            var selectedRegisters = rules.Select(rule => lookup[rule.Key])
                .Where(table => table.Role == "Main" && IsRegister(table)).ToArray();
            progress("Подсчёт удаляемых записей");
            foreach(var rule in rules)
            {
                var table=lookup[rule.Key];
                if(rule.Before is null)await Add(table,"","1=1");
                else
                {
                    var date=WorkflowRules.SqlDate(rule.Before,analysis.YearOffset);
                    var dateTable=table.DateParentKey is not null?lookup[table.DateParentKey]:table;
                    var prefix=table.DateParentKey is not null?"p":"s";
                    var from=table.DateParentKey is not null?$"JOIN {Table(dateTable)} p ON s.{Q(table.ParentLinkColumn!)}=p.{Q(Id(dateTable))}{Scope(table,dateTable,"s","p")}":"";
                    var emptyYear=1+analysis.YearOffset;
                    if(dateTable.Columns.Single(c=>c.Name==table.DateColumn).SqlType=="datetime")emptyYear=Math.Max(1753,emptyYear);
                    var empty=new DateTime(emptyYear,1,1);
                    await Add(table,from,$"{prefix}.{Q(table.DateColumn!)}>{RestoreSql.L(empty.ToString("yyyy-MM-ddTHH:mm:ss"))} AND {prefix}.{Q(table.DateColumn!)}<{RestoreSql.L(date.ToString("yyyy-MM-ddTHH:mm:ss"))}");
                }
            }
            var masters=all.Where(t=>Master(t)&&sets.ContainsKey(t.Key)).ToArray();
            progress("Подготовка связанных строк документов и справочников");
            // Only include physical child tables. Catalog items follow their own selection.
            foreach(var source in all.Where(t=>(t.Role is "Part" or "Changes" or "Journal") && !IsRegister(t) && (t.Rows>0 || sets.ContainsKey(t.Key))))
            foreach(var column in source.Columns.Where(c=>c.SqlType=="binary"&&c.Length==16&&c.Name.EndsWith("RRef",StringComparison.OrdinalIgnoreCase)))
            {
                if(column.Name=="_IDRRef" && source.Role=="Part")continue;
                var field=source.Fields.FirstOrDefault(f=>f.Column.Equals(column.Name,StringComparison.OrdinalIgnoreCase));
                var linked=source.Role=="Part"&&field?.Name=="Ссылка"
                    || source.Role=="Changes"&&column.Name=="_IDRRef" || source.Role=="Journal"&&column.Name=="_DocumentRRef";
                if(!linked)continue;
                var match=field is null?Match.Empty:Regex.Match(field.LogicalType,@"^entity\((\d+)\)$");
                var type=match.Success?int.Parse(match.Groups[1].Value):0;
                var candidate=column.Name.EndsWith("RRRef",StringComparison.OrdinalIgnoreCase)?column.Name[..^5]+"RTRef":column.Name[..^4]+"TRef";
                var typeColumn=source.Columns.Any(c=>c.Name.Equals(candidate,StringComparison.OrdinalIgnoreCase))?candidate:null;
                foreach(var target in masters.Where(t=>type==0 || t.TypeCode==type))
                {
                    var removed=sets[target.Key];
                    var join=$"s.{Q(column.Name)}=p.{Q(Id(target))}"
                        +(typeColumn is null?"":$" AND s.{Q(typeColumn)}=CONVERT(binary(4),{target.TypeCode})")
                        +Scope(source,target,"s","p");
                    await Add(source,$"JOIN {Table(target)} p ON {join} JOIN {removed.Name} removed ON {KeyJoin(target,"p","removed")}","1=1");
                }
            }
            // Registry auxiliaries are tracked by metadata, never by guessed numeric table suffixes.
            var auxiliaryDeletes = new List<FilteredDelete>();
            if (selectedRegisters.Length > 0)
                progress("Подготовка итогов и связанных таблиц регистров");
            foreach(var root in selectedRegisters)
            {
                using var measured = measureTable?.Invoke("Определение объёма и периода",root);
                currentOperation = ("подсчёт строк регистра", root);
                var set=sets[root.Key];
                var total=await Observe("count-register-rows",root,()=>RestoreSql.Scalar<long>(cn,$"SELECT COUNT_BIG(*) FROM {Table(root)}",token,tx),token);
                var removed=await Observe("count-delete-keys",root,()=>RestoreSql.Scalar<long>(cn,$"SELECT COUNT_BIG(*) FROM {set.Name}",token,tx),token);
                if(removed==0)continue;
                var full=removed==total;
                DateTime? safeAfter=null;
                if(!full && root.DateColumn is not null)
                {
                    currentOperation = ("определение периода для очистки итогов", root);
                    var value=await Observe("find-register-cutoff",root,
                        ()=>RestoreSql.Query(cn,$"SELECT MAX(s.{Q(root.DateColumn)}) AS LastDeleted FROM {Table(root)} s JOIN {set.Name} d ON {KeyJoin(root,"s","d")}",token,tx),token);
                    if(value[0]["LastDeleted"] is DateTime latest)safeAfter=latest;
                }
                foreach(var aux in all.Where(t=>t.RootKey==root.Key&&t.Role is "Totals" or "DimValues" or "Changes"))
                {
                    // These tables have a deletion predicate even when they lack a unique SQL index.
                    if (full)
                    {
                        auxiliaryDeletes.Add(new(aux,"1=1",ByPeriod:true,FullTable:true));
                    }
                    else if (aux.Role=="Changes")
                    {
                        var common=root.Keys.Where(k=>aux.Columns.Any(c=>c.Name==k)).ToArray();
                        if (common.Length>0)
                        {
                            var matches = string.Join(" AND ",common.Select(k=>$"s.{Q(k)}=d.{Q(k)}"));
                            auxiliaryDeletes.Add(new(aux,$"EXISTS (SELECT 1 FROM {set.Name} d WHERE {matches})"));
                        }
                    }
                    else if (safeAfter is { } cutoff && aux.Columns.Any(c=>c.Name=="_Period"))
                    {
                        auxiliaryDeletes.Add(new(aux,$"s.[_Period]<={PeriodLiteral(cutoff)}",ByPeriod:true,Cutoff:cutoff));
                    }
                    else throw new InputException($"Невозможно безопасно обработать связанную таблицу {aux.Table} для {root.LogicalName}.");
                }
                if(!full&&root.Kind is "РегистрНакопления" or "РегистрБухгалтерии")
                    notes.Add($"{root.LogicalName}: сохранены более поздние и текущие итоги исходной копии. Полный пересчёт итогов по усечённым движениям может изменить начальные остатки.");
            }
            if (auxiliaryDeletes.Count>0)
                progress(previewOnly ? "Подсчёт связанных строк регистров" : "Удаление связанных строк регистров");
            foreach (var deletion in auxiliaryDeletes.AsEnumerable().Reverse())
            {
                var table = deletion.Table;
                currentOperation = (previewOnly ? "подсчёт связанных строк для отчёта" : "удаление связанных строк", table);
                using var measured = measureTable?.Invoke(previewOnly ? "Подсчёт связанных строк" : "Удаление связанных строк",table);
                if (deletion.FullTable) await CaptureBinaryKeys(table,"",deletion.Predicate);
                long rows;
                if (previewOnly)
                {
                    rows = await Observe("preview-auxiliary-rows",table,
                        ()=>RestoreSql.Scalar<long>(cn,$"SELECT COUNT_BIG(*) FROM {Table(table)} s WHERE {deletion.Predicate}",token,tx),token);
                }
                else
                {
                    var rewritten = await TryRewrite(table,KeepAuxiliaryRows(deletion));
                    rows = rewritten ?? await DeleteAuxiliary(cn,tx,deletion,reportProgress,diagnostics,token);
                }
                counts.Add(new(table.LogicalName,table.Table,rows,"Связанные данные"));
            }
            if(previewOnly)
            {
                progress("Подсчёт выбранных строк для отчёта");
                foreach(var set in sets.Values)
                {
                    currentOperation = ("подсчёт строк для отчёта", set.Table);
                    using var measured = measureTable?.Invoke("Подсчёт выбранных строк",set.Table);
                    counts.Add(new(set.Table.LogicalName,set.Table.Table,await RestoreSql.Scalar<long>(cn,$"SELECT COUNT_BIG(*) FROM {set.Name}",token,tx,RestoreSql.LongCommandTimeoutSeconds),rules.Any(r=>r.Key==set.Table.Key)?"Выбрано / обязательно":"Связанные данные"));
                }
                currentOperation = null;
                await Rollback();
                return new([..counts],[..notes],counts.Sum(c=>c.Rows));
            }
            var fullyCleared = rules.Where(rule=>rule.Before is null).Select(rule=>rule.Key).ToHashSet(StringComparer.Ordinal);
            var changedTables = new HashSet<string>(StringComparer.Ordinal);
            progress("Удаление выбранных данных");
            foreach(var set in sets.Values.Reverse())
            {
                currentOperation = ("удаление строк", set.Table);
                using var measured = measureTable?.Invoke("Удаление выбранных строк",set.Table);
                var keyMatch = KeyJoin(set.Table,"s","d");
                var join = $"JOIN {set.Name} d ON {keyMatch}";
                var fullTable = fullyCleared.Contains(set.Table.Key);
                if (fullTable) await CaptureBinaryKeys(set.Table,join,"1=1");
                var keep = $"NOT EXISTS (SELECT 1 FROM {set.Name} d WHERE {keyMatch})";
                var rewritten = await TryRewrite(set.Table,keep,set.Name);
                var rows = rewritten ?? (isolatedShrink && set.Table.Rows > ShrinkDeleteBatchSize
                    ? await DeleteSelectedInBatches(set,keyMatch)
                    : await Observe("delete-selected-rows",set.Table,
                        ()=>RestoreSql.Scalar<long>(cn,$"DELETE s FROM {Table(set.Table)} s{(isolatedShrink?" WITH (TABLOCKX)":"")} {join}; SELECT ROWCOUNT_BIG()",token,tx),token));
                if (rows>0) changedTables.Add(set.Table.Key);
                counts.Add(new(set.Table.LogicalName,set.Table.Table,rows,rules.Any(r=>r.Key==set.Table.Key)?"Выбрано / обязательно":"Связанные данные"));
            }
            var rebuildingSlices = false;
            foreach(var root in selectedRegisters.Where(t=>t.Kind=="РегистрСведений" && changedTables.Contains(t.Key)))
            {
                foreach(var slice in all.Where(t=>t.RootKey==root.Key&&t.Role is "SliceFirst" or "SliceLast"))
                {
                    if (!rebuildingSlices)
                    {
                        progress("Перестроение срезов регистров");
                        rebuildingSlices = true;
                    }
                    currentOperation = ("перестроение среза", slice);
                    using var measured = measureTable?.Invoke("Перестроение среза",slice);
                    var dimensionColumns=root.Fields.Where(f=>f.Purpose.Contains("Dimension",StringComparison.Ordinal)||f.Purpose.Contains("UseDataSeparation",StringComparison.Ordinal)).Select(f=>f.Column).Distinct().ToArray();
                    if(root.DateColumn is null||dimensionColumns.Length==0||slice.Columns.Any(c=>!root.Columns.Any(m=>m.Name==c.Name)))
                        throw new InputException($"Неподдерживаемая структура среза {slice.Table}; сокращение отменено.");
                    var order=slice.Role=="SliceLast"?"DESC":"ASC";
                    var ordering=string.Join(",",new[]{root.DateColumn}.Concat(root.Keys.Where(k=>k!=root.DateColumn)).Select(k=>Q(k)+" "+order));
                    var active=root.Columns.Any(c=>c.Name=="_Active")?" WHERE [_Active]=0x01":"";
                    var names=string.Join(",",slice.Columns.Select(c=>Q(c.Name)));
                    var insert = $"INSERT INTO {Table(slice)}({names}) SELECT {names} FROM(SELECT *,ROW_NUMBER() OVER(PARTITION BY {string.Join(",",dimensionColumns.Select(Q))} ORDER BY {ordering}) AS __dr_row FROM {Table(root)}{active}) d WHERE __dr_row=1";
                    if (isolatedShrink)
                    {
                        await DeleteAuxiliary(cn,null,new(slice,"1=1"),reportProgress,diagnostics,token);
                        await Execute("rebuild-register-slice",slice,insert,token);
                    }
                    else
                    {
                        await Execute("rebuild-register-slice",slice,$"DELETE FROM {Table(slice)}; {insert}",token);
                    }
                    counts.Add(new(slice.LogicalName,slice.Table,0,"Срез перестроен по оставшимся записям"));
                }
            }
            currentOperation = null;
            if(rollbackOnly)await Rollback();
            else if (tx is not null)
            {
                progress("Фиксация сокращения");
                // SqlTransaction.Commit uses the connection timeout, just like Rollback.
                try
                {
                    await Execute("commit",null,"COMMIT TRANSACTION",token);
                    committed = true;
                }
                catch (SqlException error) when (error.Number == -2)
                {
                    throw new SqlServiceException(
                        $"SQL Server не подтвердил фиксацию сокращения за {RestoreSql.LongCommandTimeoutSeconds / 60} мин.",
                        -2, error);
                }
            }
            if (binaryCleanup is not null)
            {
                var binaryTable = rewriteTables
                    ? all.FirstOrDefault(table => table.Schema=="dbo" && table.Table=="BinaryData"
                        && ShrinkTableRewriter.IsLargeEnough(table))
                    : null;
                Func<long,long,Task<BinaryDataCleanupReport?>>? rewriteBinaryData = binaryTable is null ? null
                    : (candidates,remaining) => InShrinkTransaction(binaryTable,local =>
                        binaryCleanup.TryRewrite(binaryTable,local,diagnostics,logger,reportProgress,
                            rewriteMinDeletePercent,candidates,remaining,token));
                binaryReport = await binaryCleanup.RemoveUnreferenced(progress,reportProgress,diagnostics,logger,
                    analysis.Id,token,rewriteBinaryData);
                logger.LogInformation("Shrink BinaryData {JobId}: {@BinaryDataCleanup}",analysis.Id,binaryReport);
            }
            return new([..counts],[..notes],counts.Sum(x=>x.Rows),binaryReport);
        }
        catch (Exception error)
        {
            reductionFailed = true;
            if (tx is not null && !rollbackStarted && !committed)
            {
                try { await Rollback(); }
                catch (Exception rollbackError)
                {
                    logger.LogError(rollbackError, "Не удалось подтвердить откат сокращения базы {Database}", database);
                }
            }
            if (error is SqlException { Number: -2 } && currentOperation is { } operation)
                throw new SqlServiceException(
                    $"Тайм-аут при операции «{operation.Action}» для {operation.Table.LogicalName} ({operation.Table.Schema}.{operation.Table.Table}).",
                    -2, error);
            throw;
        }
        finally
        {
            try { tx?.Dispose(); }
            catch (Exception cleanupError) when (reductionFailed)
            {
                logger.LogError(cleanupError, "Ошибка освобождения транзакции сокращения базы {Database}", database);
            }
        }
    }
}
