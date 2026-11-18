using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevRefresh;

public sealed record AnalysisInput(string BackupName);
public sealed record BackupAliasInput(string BackupSourceKey);
public sealed record ReductionRule(string Key,string? Before);
public sealed record ProfileInput(string AnalysisId,ReductionRule[] Rules);
public sealed record ReductionProfile(string SourceKey,string AnalysisId,string BackupName,string Revision,
    ReductionRule[] Rules,string UpdatedAt,string UpdatedBy);
public sealed record RefreshInput(string Database,bool Shrink,bool Pii,string? ProfileRevision,
    ReductionRule[]? Rules,bool ConfirmReplace,bool ConfirmDisconnect,string? BackupName=null,DateTimeOffset? ScheduledAt=null,
    string? SourceDatabase=null,bool Dt=false);
public sealed record ReductionEstimateInput(string Database,string ProfileRevision,ReductionRule[] Rules,string? SourceDatabase=null);
public sealed record WorkflowRequest(SqlSettings Settings,string BackupName,ReductionProfile? Profile,
    ReductionRule[] Rules,bool Shrink,bool ConfirmDisconnect,string? SourceDatabase=null,
    bool UsePreparedShrink=false,string? BuildSlot=null,bool ResolveLatestBackup=false,bool UseDt=false,RestorePipeline? Pipeline=null);
public sealed record WorkflowJob(string Id,string Kind,string Actor,string? Target,string SourceKey,
    string Status,string Stage,string CreatedAt,string UpdatedAt,WorkflowRequest Request,
    string? Result,string? Error,string? ResourceName,string? PinnedBackup,DateTimeOffset? ScheduledAt=null,DateTimeOffset? StartedAt=null,
    DateTimeOffset? ReadyAt=null,string? BackgroundStatus=null,string? BackgroundError=null)
{
    public object Public(WorkflowExecutionReport? report=null)=>new {Id,Kind,Actor,Target,SourceKey,Status,Stage,CreatedAt,UpdatedAt,ScheduledAt,StartedAt,Error,
        Request.SourceDatabase,Request.UseDt,ReadyAt,BackgroundStatus,BackgroundError,
        FinishedAt=report?.FinishedAt,ExecutionReport=report,
        Result=Result is null?null:WebResult(JsonNode.Parse(Result))};
    private static JsonNode? WebResult(JsonNode? node)
    {
        if(node is JsonObject obj)
        {
            var result=new JsonObject();
            foreach(var item in obj)result[JsonNamingPolicy.CamelCase.ConvertName(item.Key)]=WebResult(item.Value);
            return result;
        }
        if(node is JsonArray array)
        {
            var result=new JsonArray();foreach(var item in array)result.Add(WebResult(item));return result;
        }
        return node?.DeepClone();
    }
}
public sealed record WorkflowExecutionReport(DateTimeOffset FinishedAt,long DurationMs,WorkflowStageTiming[] Stages,
    WorkflowFileSize[] FileSizes,WorkflowFileOptimization? FileOptimization,BinaryDataCleanupReport? BinaryDataCleanup=null);
public sealed record BinaryDataCleanupReport(long CandidateKeys,long RetainedKeys,long DeletedKeys,long DeletedBlocks,long PayloadBytes);
public sealed record WorkflowStageTiming(string Name,long DurationMs,WorkflowTableTiming[] Tables);
public sealed record WorkflowTableTiming(string Operation,string Object,string Table,long DurationMs);
public sealed record WorkflowFileSize(string Stage,long DataBytes,long? DataUsedBytes,long LogBytes,long? LogUsedBytes,string? LogReuseWait,
    string? RecoveryModel=null);
public sealed record WorkflowFileOptimization(long? DataTargetBytes,long LogTargetBytes);
public sealed record ColumnShape(string Name,string SqlType,int Length,bool Nullable,bool Identity,bool Computed);
public sealed record FieldShape(string Name,string Column,string Purpose,string ColumnPurpose,string LogicalType);
public sealed record AnalyzedTable(string Key,string LogicalName,string Schema,string Table,string Kind,
    string RootKey,string Role,int TypeCode,bool CanSelect,string? Reason,string? DateColumn,
    string? DateParentKey,string? ParentLinkColumn,int YearOffset,FieldShape[] Fields,ColumnShape[] Columns,
    string[] Keys,long Rows,decimal DataMB,decimal IndexMB,decimal UsedMB,decimal ReservedMB,
    PeriodDistribution? PeriodDistribution=null);
public sealed record PeriodBucket(string Month,long Rows);
public sealed record PeriodDistribution(long TotalRows,PeriodBucket[] Buckets);
public sealed record ReductionEstimate(decimal OriginalMB,decimal RemovedMinMB,decimal RemovedMaxMB,
    decimal ResultMinMB,decimal ResultMaxMB,decimal RemovedMinPercent,decimal RemovedMaxPercent,
    bool NeedsPeriodAnalysis,string AnalyzedAt,string[] Notes);
public sealed record AnalysisData(string Id,string SourceKey,string BackupName,
    string Configuration,string ConfigurationVersion,int YearOffset,string SchemaSignature,
    AnalyzedTable[] Tables,string[] Warnings,string AnalyzedAt);
public sealed record DeletionCount(string Object,string Table,long Rows,string Reason);
public sealed record ReductionResult(DeletionCount[] Deleted,string[] Notes,long TotalRows,BinaryDataCleanupReport? BinaryDataCleanup=null);

public static class WorkflowRules
{
    internal const string ObjectVersionsRegister = "РегистрСведений.ВерсииОбъектов";

    public static bool IsCopySource(string source,string target,string sourceKey) =>
        !source.Equals(target,StringComparison.OrdinalIgnoreCase)
        && Rules.ParseDatabase(target) is {IsTestContour:false}
        && Rules.ParseDatabase(source) is {IsTestContour:false} parsed && parsed.SourceKey==sourceKey;

    public static (string SourceKey, DateOnly? BackupDate)? ParseBackupName(string filename,string extension=".bak")
    {
        if (extension is not (".bak" or ".dt") || !filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            return null;

        var source = filename[..^extension.Length];
        DateOnly? backupDate = null;
        if (source.Length > 11 && source[^11] == '_' &&
            DateOnly.TryParseExact(source.AsSpan(source.Length - 10), "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            source = source[..^11];
            backupDate = date;
        }

        var key = ParseSource(source);
        return key is null ? null : (key, backupDate);
    }

    private static string? ParseSource(string input) =>
        input.Length is >= 1 and <= 64 ? Rules.ParseDatabase(input + "_dev_owner")?.SourceKey : null;

    public static string Source(string? input) => ParseSource(input?.Trim() ?? "")
        ?? throw new InputException("Тип базы: латинские буквы, цифры и подчёркивание.");

    public static bool ProfileMatchesBackup(ReductionProfile profile, string backupSourceKey) =>
        ParseBackupName(profile.BackupName)?.SourceKey == backupSourceKey;
    public static ReductionRule[] ValidateSelection(AnalysisData analysis,IEnumerable<ReductionRule> rules,
        IReadOnlySet<string>? allowed=null)
    {
        var selected=rules.ToArray();
        if(selected.Length>1000 || selected.Select(x=>x.Key).Distinct(StringComparer.Ordinal).Count()!=selected.Length)
            throw new InputException("Повторяющиеся правила либо выбрано более 1000 таблиц.");
        var lookup=analysis.Tables.ToDictionary(x=>x.Key,StringComparer.Ordinal);
        foreach(var rule in selected)
        {
            if(!lookup.TryGetValue(rule.Key,out var table) || !table.CanSelect || allowed is not null && !allowed.Contains(rule.Key))
                throw new InputException("Выбранная таблица отсутствует в доступном профиле или не допускает очистку.");
            if(table.DateColumn is not null)
            {
                if(!DateOnly.TryParseExact(rule.Before,"yyyy-MM-dd",out var date) || date.Year<1900 || date.Year+analysis.YearOffset>9999)
                    throw new InputException($"Укажите корректную дату для {table.LogicalName}.");
            }
            else if(rule.Before is not null) throw new InputException("Таблица без периода допускает только полную очистку.");
        }
        return selected;
    }
    public static DateTime SqlDate(string date,int yearOffset)
    {
        var value=DateOnly.ParseExact(date,"yyyy-MM-dd");
        return new(value.Year+yearOffset,value.Month,value.Day,0,0,0,DateTimeKind.Unspecified);
    }
    public static bool CanRead(WorkflowJob job,AppUser user)
    {
        if(user.IsAdmin)return true;
        if(job.Kind!="refresh")return false;
        var target=job.Target is null?null:Rules.ParseDatabase(job.Target);
        if(target is {IsTestContour:true})return Rules.CanAccessDatabase(target,user);
        return job.Actor==user.Name || Rules.CanAccessDatabase(target,user);
    }
}
