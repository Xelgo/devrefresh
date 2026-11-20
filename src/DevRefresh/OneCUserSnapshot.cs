using Microsoft.Data.SqlClient;

namespace DevRefresh;

// These SQL copies exist only while the platform maintenance connection is running.
internal static class OneCUserSnapshot
{
    private static string Prefix(string id)
    {
        if(!Guid.TryParseExact(id,"N",out _))throw new InputException("Некорректное задание операций 1С.");
        return "DevRefreshAuth_"+id;
    }
    private static async Task<(string Schema,string Name)> Users(SqlConnection db,CancellationToken token)
    {
        var tables=await RestoreSql.Query(db,"SELECT s.name AS s,t.name AS n FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.name IN ('v8users','_v8users')",token);
        if(tables.Count!=1)throw new InputException("Не найдена однозначная таблица пользователей 1С.");
        return ((string)tables[0]["s"]!,(string)tables[0]["n"]!);
    }
    private static string Q(string name)=>RestoreSql.Q(name);
    private static async Task<string> Columns(SqlConnection db,string table,CancellationToken token)
    {
        var columns=await RestoreSql.Query(db,"SELECT name FROM sys.columns WHERE object_id=OBJECT_ID("+RestoreSql.L(table)+") AND is_computed=0 ORDER BY column_id",token);
        return string.Join(",",columns.Select(row=>Q((string)row["name"]!)));
    }
    public static async Task SaveAndClear(SqlConnection db,string id,CancellationToken token)
    {
        var prefix=Prefix(id);var table=await Users(db,token);
        var users=Q(table.Schema)+"."+Q(table.Name);
        var keys=await RestoreSql.Scalar<int>(db,"SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.V8USERSMATKEYS')",token)==1;
        await using var tx=(SqlTransaction)await db.BeginTransactionAsync(token);
        await RestoreSql.Execute(db,$"""
            SELECT * INTO dbo.{Q(prefix+"_users")} FROM {users} WITH(TABLOCKX,HOLDLOCK);
            SELECT * INTO dbo.{Q(prefix+"_params")} FROM dbo.Params WITH(UPDLOCK,HOLDLOCK) WHERE FileName=N'users.usr';
            {(keys?$"SELECT * INTO dbo.{Q(prefix+"_keys")} FROM dbo.V8USERSMATKEYS WITH(TABLOCKX,HOLDLOCK);":"")}
            DELETE FROM {users};
            DELETE FROM dbo.Params WHERE FileName=N'users.usr';
            """,token,tx);
        await tx.CommitAsync(token);
    }
    public static async Task Restore(SqlConnection db,string id,CancellationToken token)
    {
        var prefix=Prefix(id);
        if(await RestoreSql.Scalar<int>(db,"SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID("+RestoreSql.L("dbo."+prefix+"_users")+")",token)==0)return;
        var table=await Users(db,token);var users=Q(table.Schema)+"."+Q(table.Name);
        var userColumns=await Columns(db,users,token);var paramColumns=await Columns(db,"dbo.Params",token);
        var keys=await RestoreSql.Scalar<int>(db,"SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID("+RestoreSql.L("dbo."+prefix+"_keys")+")",token)==1;
        var keyColumns=keys?await Columns(db,"dbo.V8USERSMATKEYS",token):"";
        await using var tx=(SqlTransaction)await db.BeginTransactionAsync(token);
        await RestoreSql.Execute(db,$"""
            DELETE FROM {users} WITH(TABLOCKX);
            INSERT {users}({userColumns}) SELECT {userColumns} FROM dbo.{Q(prefix+"_users")};
            DELETE FROM dbo.Params WHERE FileName=N'users.usr';
            INSERT dbo.Params({paramColumns}) SELECT {paramColumns} FROM dbo.{Q(prefix+"_params")};
            {(keys?$"DELETE FROM dbo.V8USERSMATKEYS; INSERT dbo.V8USERSMATKEYS({keyColumns}) SELECT {keyColumns} FROM dbo.{Q(prefix+"_keys")};":"")}
            """,token,tx);
        await tx.CommitAsync(token);
    }
    public static Task Remove(SqlConnection db,string id,CancellationToken token)
    {
        var prefix=Prefix(id);
        return RestoreSql.Execute(db,$"DROP TABLE IF EXISTS dbo.{Q(prefix+"_users")},dbo.{Q(prefix+"_params")},dbo.{Q(prefix+"_keys")};",token);
    }
}
