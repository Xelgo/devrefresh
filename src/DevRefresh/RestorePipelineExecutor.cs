using System.Data;
using Microsoft.Data.SqlClient;

namespace DevRefresh;

internal sealed record DomainChange(byte[] Id,string Before,string After);

public sealed class RestorePipelineExecutor
{
    internal static DomainChange[] Plan(IEnumerable<(byte[] Id,string OSName)> users,string domain,int maxLength)
    {
        RestorePipelineRules.ValidateDomain(domain);
        var rows=users.Select(row=>new DomainChange(row.Id,row.OSName,
            RestorePipelineRules.ReplaceDomain(row.OSName,domain)
                ??throw new InputException("В v8users найден неизвестный формат OSName. Подмена домена не выполнена."))).ToArray();
        if(rows.Any(x=>x.After.Length>maxLength))throw new InputException("Новое имя ОС не помещается в OSName. Подмена домена не выполнена.");
        if(rows.GroupBy(x=>x.After.TrimStart('\\'),StringComparer.OrdinalIgnoreCase)
            .Any(group=>group.Count()>1 && group.Any(x=>x.Before!=x.After)))
            throw new InputException("После подмены домена несколько пользователей 1С получат одну учётную запись ОС. Изменения отменены.");
        return rows;
    }

    public async Task<RestoreOperationResult[]> Apply(SqlConnection connection,RestorePipeline? pipeline,Action<string> progress,CancellationToken token)
    {
        if(pipeline is null || pipeline.Operations.Length==0)return [];
        if(!connection.Database.Equals(pipeline.Database,StringComparison.OrdinalIgnoreCase))
            throw new InputException("Правило операций относится к другой SQL-базе.");
        foreach(var operation in pipeline.Operations)
        {
            RestorePipelineRules.Validate(pipeline.Database,pipeline.SourceKind,operation.TargetDomain);
            if(operation.Kind!=RestorePipelineRules.WindowsDomain)throw new InputException("Неизвестная операция пайплайна восстановления.");
        }
        var result=new List<RestoreOperationResult>();
        // Commit the whole ordered chain together, before publishing database readiness.
        await using var tx=(SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,token);
        foreach(var operation in pipeline.Operations)
        {
            progress("Операции после восстановления · Подмена домена ОС");
            var tables=await RestoreSql.Query(connection,"""
                SELECT s.name AS schema_name,t.name AS table_name,c.max_length
                FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
                JOIN sys.columns c ON c.object_id=t.object_id AND c.name='OSName' AND c.system_type_id=231
                WHERE t.name IN ('v8users','_v8users')
                AND EXISTS(SELECT 1 FROM sys.columns i WHERE i.object_id=t.object_id AND i.name='ID' AND i.system_type_id=173 AND i.max_length=16)
                """,token,tx);
            if(tables.Count!=1)throw new InputException("Не найдена однозначная служебная таблица пользователей 1С с полями ID и OSName (nvarchar).");
            var table=tables[0];var name=RestoreSql.Q((string)table["schema_name"]!)+"."+RestoreSql.Q((string)table["table_name"]!);
            var max=Convert.ToInt32(table["max_length"]);max=max<0?int.MaxValue:max/2;
            var rows=await RestoreSql.Query(connection,$"SELECT ID,OSName FROM {name} WITH(UPDLOCK,HOLDLOCK) WHERE OSName IS NOT NULL AND OSName<>N''",token,tx);
            var plan=Plan(rows.Select(row=>((byte[])row["ID"]!,(string)row["OSName"]!)),operation.TargetDomain,max);
            var changed=0;
            await using var cmd=new SqlCommand($"UPDATE {name} SET OSName=@new WHERE ID=@id AND OSName=@old",connection,tx)
                {CommandTimeout=RestoreSql.LongCommandTimeoutSeconds};
            var id=cmd.Parameters.Add("@id",SqlDbType.Binary,16);
            var before=cmd.Parameters.Add("@old",SqlDbType.NVarChar,max==int.MaxValue?-1:max);
            var after=cmd.Parameters.Add("@new",SqlDbType.NVarChar,max==int.MaxValue?-1:max);
            foreach(var change in plan.Where(x=>x.Before!=x.After))
            {
                token.ThrowIfCancellationRequested();id.Value=change.Id;before.Value=change.Before;after.Value=change.After;
                if(await cmd.ExecuteNonQueryAsync(token)!=1)throw new InputException("Пользователь изменился во время подмены домена. Операция отменена.");
                changed++;
            }
            result.Add(new(operation.Kind,changed,plan.Length-changed,operation.TargetDomain));
        }
        await tx.CommitAsync(token);return result.ToArray();
    }
}
