using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevRefresh;

public sealed record RestoreOperation(string Kind,string TargetDomain);
public sealed record RestorePipeline(string Server,string SourceType,string ArchiveFormat,RestoreOperation[] Operations,
    string UpdatedAt,string UpdatedBy,string TargetDomain="CORP",string? MigrationNotice=null);
public sealed record RestorePipelineInput(string SourceType,string ArchiveFormat,bool RewriteDomain,string TargetDomain="");
public sealed record RestoreOperationResult(string Kind,int Changed,int Unchanged,string TargetDomain);

public static class RestorePipelineRules
{
    public const string WindowsDomain="windows-domain";
    public static string Source(WorkflowRequest request) => request.UseDt?"dt":"bak";
    internal static void ValidateDomain(string domain)
    {
        if(!Regex.IsMatch(domain,@"\A[a-zA-Z0-9](?:[a-zA-Z0-9.-]{0,251}[a-zA-Z0-9])?\z",RegexOptions.CultureInvariant))
            throw new InputException("Укажите домен без обратных слешей и имени пользователя, например CORP.");
    }
    public static void Validate(string sourceType,string archiveFormat,string? domain=null)
    {
        _=WorkflowRules.Source(sourceType);
        if(archiveFormat is not ("bak" or "dt"))throw new InputException("Выберите формат архива .bak или .dt.");
        if(domain is not null)ValidateDomain(domain);
    }
}

public sealed partial class Store
{
    public RestorePipeline[] GetRestorePipelines()
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT value FROM restore_pipelines ORDER BY server,source_type,archive_format";
        using var reader=cmd.ExecuteReader();var result=new List<RestorePipeline>();
        while(reader.Read())result.Add(JsonSerializer.Deserialize<RestorePipeline>(reader.GetString(0))!);
        return result.ToArray();
    }
    public RestorePipeline? GetRestorePipeline(string server,string sourceType,string archiveFormat)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT value FROM restore_pipelines WHERE server=$server AND source_type=$type AND archive_format=$format";
        cmd.Parameters.AddWithValue("$server",server);cmd.Parameters.AddWithValue("$type",sourceType);cmd.Parameters.AddWithValue("$format",archiveFormat);
        return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize<RestorePipeline>(json):null;
    }
    public RestorePipeline SaveRestorePipeline(RestorePipelineInput input,string actor)
    {
        var type=WorkflowRules.Source(input.SourceType);var format=input.ArchiveFormat.Trim().TrimStart('.').ToLowerInvariant();
        var domain=input.TargetDomain.Trim();if(domain.Length==0 && !input.RewriteDomain)domain="CORP";
        RestorePipelineRules.Validate(type,format,domain);
        var server=GetSettings()?.Server??throw new InputException("Сначала настройте SQL Server.");
        var value=new RestorePipeline(server,type,format,input.RewriteDomain?[new(RestorePipelineRules.WindowsDomain,domain)]:[],
            DateTimeOffset.UtcNow.ToString("O"),actor,domain);
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO restore_pipelines(server,source_type,archive_format,value) VALUES($server,$type,$format,$value)
            ON CONFLICT(server,source_type,archive_format) DO UPDATE SET value=excluded.value
            """;
        cmd.Parameters.AddWithValue("$server",server);cmd.Parameters.AddWithValue("$type",type);
        cmd.Parameters.AddWithValue("$format",format);cmd.Parameters.AddWithValue("$value",JsonSerializer.Serialize(value));cmd.ExecuteNonQuery();
        WriteAudit(db,tx,actor,"restore.pipeline.saved",$"Операции после восстановления: тип {type}, .{format}; подмена домена: {(input.RewriteDomain?domain:"выключена")}.");
        tx.Commit();return value;
    }
    public void DeleteRestorePipeline(string server,string sourceType,string format,string actor)
    {
        RestorePipelineRules.Validate(sourceType,format);
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="DELETE FROM restore_pipelines WHERE server=$server AND source_type=$type AND archive_format=$format";
        cmd.Parameters.AddWithValue("$server",server);cmd.Parameters.AddWithValue("$type",sourceType);cmd.Parameters.AddWithValue("$format",format);
        cmd.ExecuteNonQuery();WriteAudit(db,tx,actor,"restore.pipeline.deleted",$"Правило операций удалено: {sourceType}, .{format}.");tx.Commit();
    }

    private sealed record LegacyPipeline(string Server,string Database,string SourceKind,RestoreOperation[] Operations,
        string UpdatedAt,string UpdatedBy,string TargetDomain="CORP");
    private void MigrateLegacyRestorePipelines()
    {
        using var db=Open();using var tx=db.BeginTransaction();
        using var read=db.CreateCommand();read.Transaction=tx;
        read.CommandText="SELECT value FROM restore_pipelines_legacy WHERE migrated=0";
        var old=new List<LegacyPipeline>();
        using(var reader=read.ExecuteReader())while(reader.Read())old.Add(JsonSerializer.Deserialize<LegacyPipeline>(reader.GetString(0))!);
        if(old.Count==0)return;
        var aliases=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        read.CommandText="SELECT target_type,backup_type FROM backup_aliases";
        using(var reader=read.ExecuteReader())while(reader.Read())aliases[reader.GetString(0)]=reader.GetString(1);
        var groups=old.GroupBy(value=>
        {
            var type=Rules.ParseDatabase(value.Database)?.SourceKey??throw new InputException("Неизвестный тип в старом правиле операций.");
            return (Server:value.Server.ToLowerInvariant(),Type:aliases.GetValueOrDefault(type,type),Format:value.SourceKind=="dt"?"dt":"bak");
        });
        foreach(var group in groups)
        {
            var first=group.First();
            var variants=group.Select(x=>JsonSerializer.Serialize(x.Operations.Select(op=>new {Kind=op.Kind.ToLowerInvariant(),Domain=op.TargetDomain.ToUpperInvariant()}))).Distinct().Count();
            var conflict=variants>1;
            var pipeline=new RestorePipeline(first.Server,group.Key.Type,group.Key.Format,conflict?[]:first.Operations,
                DateTimeOffset.UtcNow.ToString("O"),"system",first.TargetDomain,
                conflict?"Старые правила для баз или Full/Shrink различались. Подмена отключена: выберите общий домен и сохраните правило.":null);
            using var insert=db.CreateCommand();insert.Transaction=tx;
            insert.CommandText="INSERT OR IGNORE INTO restore_pipelines(server,source_type,archive_format,value) VALUES($server,$type,$format,$value)";
            insert.Parameters.AddWithValue("$server",pipeline.Server);insert.Parameters.AddWithValue("$type",pipeline.SourceType);
            insert.Parameters.AddWithValue("$format",pipeline.ArchiveFormat);insert.Parameters.AddWithValue("$value",JsonSerializer.Serialize(pipeline));insert.ExecuteNonQuery();
        }
        read.CommandText="UPDATE restore_pipelines_legacy SET migrated=1 WHERE migrated=0";read.ExecuteNonQuery();
        WriteAudit(db,tx,"system","restore.pipeline.migrated","Правила операций перенесены на вид базы и формат архива.");tx.Commit();
    }
}
