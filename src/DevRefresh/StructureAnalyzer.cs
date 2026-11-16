using System.Security.Cryptography;
using System.Text;
using DaJet.Data;
using DaJet.Metadata;
using DaJet.TypeSystem;
using Microsoft.Data.SqlClient;

namespace DevRefresh;

public sealed class StructureAnalyzer(RestoreSql sql)
{
    public async Task<AnalysisData> Analyze(SqlSettings settings,string database,string id,string source,
        string backupName,CancellationToken token,bool includePeriodStatistics=false,bool liveDatabase=false)
    {
        await using var cn=await sql.Open(settings,database,token);
        if(liveDatabase)
            await RestoreSql.Execute(cn,"SET DEADLOCK_PRIORITY LOW; SET LOCK_TIMEOUT 3000;",token);
        var raw=await RestoreSql.Query(cn,"""
SELECT s.name AS SchemaName,t.name AS TableName,c.name AS ColumnName,ty.name AS SqlType,
 c.max_length AS Length,c.is_nullable AS Nullable,c.is_identity AS IsIdentity,c.is_computed AS Computed
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
WHERE t.is_ms_shipped=0 ORDER BY t.name,c.column_id
""",token);
        var shapes=raw.GroupBy(x=>(Schema:(string)x["SchemaName"]!,Table:(string)x["TableName"]!))
            .ToDictionary(x=>x.Key,x=>x.Select(c=>new ColumnShape((string)c["ColumnName"]!, (string)c["SqlType"]!,Convert.ToInt32(c["Length"]),Convert.ToBoolean(c["Nullable"]),Convert.ToBoolean(c["IsIdentity"]),Convert.ToBoolean(c["Computed"]))).ToArray());
        var indexes=await RestoreSql.Query(cn,"""
SELECT s.name AS SchemaName,t.name AS TableName,i.index_id,i.is_primary_key,c.name AS ColumnName,ic.key_ordinal
FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.is_unique=1 AND i.has_filter=0 AND i.is_disabled=0 AND ic.key_ordinal>0
ORDER BY i.is_primary_key DESC,i.index_id,ic.key_ordinal
""",token);
        var keyLookup=indexes.GroupBy(x=>(Schema:(string)x["SchemaName"]!,Table:(string)x["TableName"]!))
            .ToDictionary(g=>g.Key,g=>g.GroupBy(x=>Convert.ToInt32(x["index_id"])).First().OrderBy(x=>Convert.ToInt32(x["key_ordinal"])).Select(x=>(string)x["ColumnName"]!).ToArray());
        var statistics=await RestoreSql.Query(cn,"""
SELECT s.name AS SchemaName,t.name AS TableName,
 SUM(CASE WHEN p.index_id IN(0,1) THEN p.row_count ELSE 0 END) AS Rows,
 CONVERT(decimal(18,3),SUM(CASE WHEN p.index_id IN(0,1) THEN p.in_row_data_page_count+p.lob_used_page_count+p.row_overflow_used_page_count ELSE 0 END)*8.0/1024) AS DataMB,
 CONVERT(decimal(18,3),SUM(p.used_page_count)*8.0/1024) AS UsedMB,
 CONVERT(decimal(18,3),SUM(p.reserved_page_count)*8.0/1024) AS ReservedMB
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.dm_db_partition_stats p ON p.object_id=t.object_id
WHERE t.is_ms_shipped=0 GROUP BY s.name,t.name
""",token);
        var sizes=statistics.ToDictionary(x=>((string)x["SchemaName"]!,(string)x["TableName"]!));
        var metadataSettings=new SqlConnectionStringBuilder(sql.ConnectionString(settings,database))
            {Pooling=true,MinPoolSize=0,MaxPoolSize=8,ApplicationName="DevRefresh.Metadata"};
        if(liveDatabase)
        {
            metadataSettings.ApplicationName="DevRefresh.DevAnalysis";
            metadataSettings.CommandTimeout=15;
        }
        var connectionString=metadataSettings.ConnectionString;
        using var metadataPool=new MetadataPool(connectionString);
        var provider=await Task.Run(()=>MetadataProvider.Create(DataSourceType.SqlServer,in connectionString),token);
        var configuration=provider.GetConfiguration();var warnings=new List<string>();
        var known=new Dictionary<string,AnalyzedTable>(StringComparer.OrdinalIgnoreCase);
        foreach(var kind in MetadataNames.GetApplicationObjectNames())
        {
            token.ThrowIfCancellationRequested();
            foreach(var name in provider.GetMetadataNames(kind))
            {
                if(liveDatabase)token.ThrowIfCancellationRequested();
                var logical=$"{kind}.{name}";
                try
                {
                    var entry=provider.GetMetadataEntry(logical);var entity=provider.GetMetadataObject(logical);
                    if(entity is null)continue;
                    Append(entity,logical,logical,kind,"Main",entry?.Code??0,null);
                    var suffixes=kind switch
                    {
                        "РегистрСведений"=>new[]{"СрезПервых","СрезПоследних","Настройки"},
                        "РегистрНакопления"=>new[]{"Итоги","Настройки"},
                        "РегистрБухгалтерии"=>new[]{"ЗначенияСубконто","ИтогиПоСчетам","ИтогиМеждуСчетами","ИтогиПоСубконто1","ИтогиПоСубконто2","ИтогиПоСубконто3","Настройки"},
                        _=>Array.Empty<string>()
                    };
                    if(kind is "Справочник" or "Документ" or "РегистрСведений" or "РегистрНакопления" or "РегистрБухгалтерии")suffixes=[..suffixes,"Изменения"];
                    foreach(var suffix in suffixes)
                    {
                        var child=provider.GetMetadataObject(logical+"."+suffix);
                        if(child is null)continue;
                        var role=suffix=="Изменения"?"Changes":suffix=="Настройки"?"Settings":suffix=="СрезПервых"?"SliceFirst":suffix=="СрезПоследних"?"SliceLast":suffix=="ЗначенияСубконто"?"DimValues":"Totals";
                        Append(child,logical+"."+suffix,logical,kind,role,entry?.Code??0,null);
                    }
                }
                catch(Exception ex) when(ex is not OperationCanceledException)
                {warnings.Add($"Не удалось разобрать {logical}: {ex.GetType().Name}. Объект недоступен для очистки.");}
            }
        }
        void Append(EntityDefinition entity,string logical,string root,string kind,string role,int code,EntityDefinition? parent)
        {
            var pair=shapes.Keys.FirstOrDefault(x=>x.Schema=="dbo"&&x.Table.Equals(entity.DbName,StringComparison.OrdinalIgnoreCase));
            if(pair.Table is null)return;
            var columns=shapes[pair];var fields=entity.Properties.SelectMany(p=>p.Columns.Select(c=>new FieldShape(p.Name,c.Name,p.Purpose.ToString(),c.Purpose.ToString(),p.Type.ToString()))).ToArray();
            string? date=null;string? parentKey=null;string? parentLink=null;
            if(role=="Main")date=fields.FirstOrDefault(x=>x.Name==(kind=="Документ"?"Дата":"Период") && columns.Any(c=>c.Name==x.Column&&c.SqlType.StartsWith("date",StringComparison.Ordinal)))?.Column;
            if(role=="Part" && parent is not null)
            {
                date=parent.Properties.FirstOrDefault(p=>p.Name=="Дата")?.Columns.FirstOrDefault()?.Name;
                if(date is not null){parentKey=root;parentLink=fields.FirstOrDefault(x=>x.Name=="Ссылка")?.Column;}
            }
            var selectable=kind is "Справочник" or "Документ" or "РегистрСведений" or "РегистрНакопления" or "РегистрБухгалтерии" && role is "Main" or "Part";
            string? reason=null;
            if(!selectable)reason=role is not("Main" or "Part")?"Связанная таблица: выбирается через основной объект.":"Этот тип метаданных не допускает очистку.";
            var keys=keyLookup.GetValueOrDefault(pair)??[];
            if(selectable && keys.Length==0){selectable=false;reason="Не найден однозначный ключ строк; очистка недоступна.";}
            if(logical==WorkflowRules.ObjectVersionsRegister)date=null;
            if(fields.Any(f=>!columns.Any(c=>c.Name.Equals(f.Column,StringComparison.OrdinalIgnoreCase)))){selectable=false;reason="Метаданные не совпали с физической структурой.";}
            var size=sizes.GetValueOrDefault(pair);var used=size is null?0:Convert.ToDecimal(size["UsedMB"]);var data=size is null?0:Convert.ToDecimal(size["DataMB"]);
            var table=new AnalyzedTable(logical,logical,pair.Schema,pair.Table,kind,root,role,code,selectable,reason,date,parentKey,parentLink,configuration.YearOffset,fields,columns,keys,size is null?0:Convert.ToInt64(size["Rows"]),data,used-data,used,size is null?0:Convert.ToDecimal(size["ReservedMB"]));
            known[pair.Table]=table;
            if(role=="Main")foreach(var part in entity.Entities)Append(part,logical+"."+part.Name,root,kind,"Part",code,entity);
        }
        foreach(var pair in shapes.Keys.Where(x=>!known.ContainsKey(x.Table)))
        {
            var size=sizes.GetValueOrDefault(pair);var used=size is null?0:Convert.ToDecimal(size["UsedMB"]);var data=size is null?0:Convert.ToDecimal(size["DataMB"]);
            var journal=pair.Table.StartsWith("_DocumentJournal",StringComparison.OrdinalIgnoreCase);
            known[pair.Table]=new("sql:"+pair.Schema+"."+pair.Table,journal?"Журнал документов":"Служебная / несопоставленная таблица",pair.Schema,pair.Table,journal?"Журнал":"Служебная", "",journal?"Journal":"System",0,false,"Прямая очистка недоступна.",null,null,null,configuration.YearOffset,[],shapes[pair],keyLookup.GetValueOrDefault(pair)??[],size is null?0:Convert.ToInt64(size["Rows"]),data,used-data,used,size is null?0:Convert.ToDecimal(size["ReservedMB"]));
        }
        // Never offer a main register whose auxiliary metadata could not be parsed.
        if(warnings.Count>0)
            foreach(var pair in known.ToArray())if(warnings.Any(w=>w.Contains(pair.Value.RootKey+":",StringComparison.Ordinal)))known[pair.Key]=pair.Value with{CanSelect=false,Reason="Ошибка разбора связанных таблиц объекта."};
        var analyzed=known.Values.OrderByDescending(t=>t.UsedMB).ToArray();
        if(includePeriodStatistics)
            for(var i=0;i<analyzed.Length;i++)
            {
                var table=analyzed[i];
                if(!table.CanSelect || table.DateColumn is null)continue;
                if(table.Rows==0){analyzed[i]=table with{PeriodDistribution=new(0,[])};continue;}
                try
                {
                    var owner=table.DateParentKey is null?table:known.Values.Single(t=>t.Key==table.DateParentKey);
                    var prefix=table.DateParentKey is null?"s":"p";
                    var emptyYear=1+table.YearOffset;
                    if(owner.Columns.Single(c=>c.Name==table.DateColumn).SqlType=="datetime")emptyYear=Math.Max(1753,emptyYear);
                    var empty=new DateTime(emptyYear,1,1).ToString("yyyy-MM-ddTHH:mm:ss");
                    var value=prefix+"."+RestoreSql.Q(table.DateColumn);
                    var year=$"CASE WHEN {value}>{RestoreSql.L(empty)} THEN YEAR({value})-{table.YearOffset} END";
                    var month=$"CASE WHEN {value}>{RestoreSql.L(empty)} THEN MONTH({value}) END";
                    var scope=string.Concat(owner.Fields.Where(f=>f.Purpose.Contains("UseDataSeparation",StringComparison.Ordinal))
                        .Where(f=>table.Columns.Any(c=>c.Name==f.Column)).Select(f=>$" AND s.{RestoreSql.Q(f.Column)}=p.{RestoreSql.Q(f.Column)}"));
                    var join=table.DateParentKey is null?"":$" LEFT JOIN {RestoreSql.Q(owner.Schema)}.{RestoreSql.Q(owner.Table)} p ON s.{RestoreSql.Q(table.ParentLinkColumn!)}=p.[_IDRRef]{scope}";
                    var query=$"SELECT {year} AS Y,{month} AS M,COUNT_BIG(*) AS N FROM {RestoreSql.Q(table.Schema)}.{RestoreSql.Q(table.Table)} s{join} GROUP BY {year},{month}";
                    await using var cmd=new SqlCommand(query,cn){CommandTimeout=RestoreSql.LongCommandTimeoutSeconds};
                    await using var reader=await cmd.ExecuteReaderAsync(token);
                    long total=0;var buckets=new List<PeriodBucket>();
                    while(await reader.ReadAsync(token))
                    {
                        var count=reader.GetInt64(2);total+=count;
                        if(!reader.IsDBNull(0)&&!reader.IsDBNull(1))
                            buckets.Add(new(new DateOnly(reader.GetInt32(0),reader.GetInt32(1),1).ToString("yyyy-MM-dd"),count));
                    }
                    analyzed[i]=table with{PeriodDistribution=new(total,[..buckets])};
                }
                catch(SqlException ex){warnings.Add($"Статистика дат для {table.LogicalName} недоступна (SQL {ex.Number}); оценка размера будет диапазоном.");}
            }
        return new(id,source,backupName,configuration.Name,configuration.AppConfigVersion,configuration.YearOffset,
            Signature(analyzed),analyzed,[..warnings],DateTimeOffset.UtcNow.ToString("O"));
    }
    public static string Signature(IEnumerable<AnalyzedTable> tables)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",tables.OrderBy(x=>x.Schema+"."+x.Table,StringComparer.Ordinal).SelectMany(t=>t.Columns.Select(c=>$"{t.Schema}.{t.Table}.{c.Name}|{c.SqlType}|{c.Length}|{c.Nullable}|{c.Identity}|{c.Computed}"))))));
    private sealed class MetadataPool(string connectionString):IDisposable
    {
        public void Dispose(){using var connection=new SqlConnection(connectionString);SqlConnection.ClearPool(connection);}
    }
}
