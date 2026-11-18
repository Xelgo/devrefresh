using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevRefresh;

public sealed record RestoreOperation(string Kind,string TargetDomain);
public sealed record RestorePipeline(string Server,string Database,string SourceKind,RestoreOperation[] Operations,
    string UpdatedAt,string UpdatedBy,string TargetDomain="CORP");
public sealed record RestorePipelineInput(string Database,string SourceKind,bool RewriteDomain,string TargetDomain="");
public sealed record RestoreOperationResult(string Kind,int Changed,int Unchanged,string TargetDomain);

public static class RestorePipelineRules
{
    public const string WindowsDomain="windows-domain";
    public static string Source(WorkflowRequest request) => request.UseDt?"dt":
        request.UsePreparedShrink || request.Shrink?"shrink":request.SourceDatabase is not null?"database":"full";
    internal static void ValidateDomain(string domain)
    {
        if(!Regex.IsMatch(domain,@"\A[a-zA-Z0-9](?:[a-zA-Z0-9.-]{0,251}[a-zA-Z0-9])?\z",RegexOptions.CultureInvariant))
            throw new InputException("Укажите домен без обратных слешей и имени пользователя, например CORP.");
    }
    public static void Validate(string database,string sourceKind,string? domain=null)
    {
        if(database.Length>128 || Rules.ParseDatabase(database) is null)throw new InputException("Укажите точное имя Dev- или _tk-базы.");
        if(sourceKind is not ("full" or "shrink" or "dt" or "database"))
            throw new InputException("Выберите источник Full, Shrink, .dt или другую Dev-базу.");
        if(domain is not null)ValidateDomain(domain);
    }
    internal static string? ReplaceDomain(string value,string domain)
    {
        var prefix=0;while(prefix<value.Length && value[prefix]=='\\')prefix++;
        if(prefix>2 || value.Any(char.IsControl))return null;
        var separator=value.IndexOf('\\',prefix);
        if(separator<=prefix || separator==value.Length-1 || value.IndexOf('\\',separator+1)>=0)return null;
        if(value[prefix..separator].Equals(domain,StringComparison.OrdinalIgnoreCase))return value;
        return value[..prefix]+domain+value[separator..];
    }
}

public sealed partial class Store
{
    public RestorePipeline[] GetRestorePipelines()
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT value FROM restore_pipelines ORDER BY server,database_name,source_kind";
        using var reader=cmd.ExecuteReader();var result=new List<RestorePipeline>();
        while(reader.Read())result.Add(JsonSerializer.Deserialize<RestorePipeline>(reader.GetString(0))!);
        return result.ToArray();
    }
    public RestorePipeline? GetRestorePipeline(string server,string database,string sourceKind)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT value FROM restore_pipelines WHERE server=$server AND database_name=$database AND source_kind=$source";
        cmd.Parameters.AddWithValue("$server",server);cmd.Parameters.AddWithValue("$database",database);cmd.Parameters.AddWithValue("$source",sourceKind);
        return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize<RestorePipeline>(json):null;
    }
    public RestorePipeline SaveRestorePipeline(RestorePipelineInput input,string actor)
    {
        var database=input.Database.Trim();var source=input.SourceKind;
        var domain=input.TargetDomain.Trim();
        if(domain.Length==0 && !input.RewriteDomain)domain="CORP";
        RestorePipelineRules.Validate(database,source,domain);
        var server=GetSettings()?.Server??throw new InputException("Сначала настройте SQL Server.");
        var value=new RestorePipeline(server,database,source,input.RewriteDomain?[new(RestorePipelineRules.WindowsDomain,domain)]:[],
            DateTimeOffset.UtcNow.ToString("O"),actor,domain);
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO restore_pipelines(server,database_name,source_kind,value) VALUES($server,$database,$source,$value)
            ON CONFLICT(server,database_name,source_kind) DO UPDATE SET value=excluded.value
            """;
        cmd.Parameters.AddWithValue("$server",server);cmd.Parameters.AddWithValue("$database",database);
        cmd.Parameters.AddWithValue("$source",source);cmd.Parameters.AddWithValue("$value",JsonSerializer.Serialize(value));cmd.ExecuteNonQuery();
        WriteAudit(db,tx,actor,"restore.pipeline.saved",$"Операции после восстановления: {database}, источник {source}; подмена домена: {(input.RewriteDomain?domain:"выключена")}.");
        tx.Commit();return value;
    }
    public void DeleteRestorePipeline(string server,string database,string source,string actor)
    {
        RestorePipelineRules.Validate(database,source);
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="DELETE FROM restore_pipelines WHERE server=$server AND database_name=$database AND source_kind=$source";
        cmd.Parameters.AddWithValue("$server",server);cmd.Parameters.AddWithValue("$database",database);cmd.Parameters.AddWithValue("$source",source);
        cmd.ExecuteNonQuery();WriteAudit(db,tx,actor,"restore.pipeline.deleted",$"Правило операций удалено: {database}, {source}.");tx.Commit();
    }
}
