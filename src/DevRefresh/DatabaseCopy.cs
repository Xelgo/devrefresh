using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using static DevRefresh.RestoreSql;

namespace DevRefresh;

internal sealed class DatabaseCopy(RestoreSql sql)
{
    public static string PreviousName(string id) => "DevRefreshPrevious_" + id;

    public async Task Transfer(SqlSettings settings, string source, string target, string stage, string id,
        Action<string> progress, CancellationToken token)
    {
        if (!Guid.TryParseExact(id,"N",out _) || stage!="DevRefreshStage_"+id ||
            Rules.ParseDatabase(target) is not { } parsed || !WorkflowRules.IsCopySource(source,target,parsed.SourceKey))
            throw new InputException("Выберите другую Dev-базу того же типа.");

        await using var control = await sql.Open(settings,token:token);
        if (!await TryRefreshLock(control,source,"Shared",token))
            throw new InputException("База-источник сейчас обновляется.");
        var info = (await Query(control,$"""
            SELECT collation_name,compatibility_level,is_read_committed_snapshot_on,snapshot_isolation_state
            FROM sys.databases
            WHERE name={L(source)} AND database_id>4 AND state=0 AND source_database_id IS NULL
            """,token)).SingleOrDefault();
        if (info?["collation_name"] is not string collation || !Regex.IsMatch(collation,@"\A[A-Za-z0-9_]+\z"))
            throw new InputException("База-источник недоступна или SQL Server не сообщил её сортировку.");

        progress("Создание базы для копирования в режиме SIMPLE");
        await Execute(control,$"CREATE DATABASE {Q(stage)} COLLATE {collation}",token);
        await Execute(control,$"ALTER DATABASE {Q(stage)} SET RECOVERY SIMPLE",token);

        progress("Онлайн-копирование структуры и данных базы");
        using var sourceConnection = new SqlConnection(sql.ConnectionString(settings));
        using var targetConnection = new SqlConnection(sql.ConnectionString(settings));
        var sourceSmo = new ServerConnection(sourceConnection) { StatementTimeout=LongCommandTimeoutSeconds };
        var targetSmo = new ServerConnection(targetConnection) { StatementTimeout=LongCommandTimeoutSeconds };
        using var cancellation = token.Register(() =>
        {
            // SMO exposes synchronous transfer. Cancel both connections to interrupt scripts or bulk copy.
            sourceConnection.Close();
            targetConnection.Close();
        });
        try
        {
            await Task.Run(() =>
            {
                var database = new Server(sourceSmo).Databases[source] ??
                    throw new InputException("База-источник исчезла перед копированием.");
                var transfer = new Transfer(database)
                {
                    CopyAllObjects=true, CopySchema=true, CopyData=true,
                    DestinationServer=settings.Server, DestinationDatabase=stage,
                    DestinationServerConnection=targetSmo, DestinationLoginSecure=settings.Authentication=="windows",
                    UseDestinationTransaction=true, BulkCopyTimeout=LongCommandTimeoutSeconds
                };
                transfer.Options.WithDependencies=true;
                transfer.Options.ContinueScriptingOnError=false;
                transfer.Options.DriAll=true;
                transfer.Options.Indexes=true;
                transfer.Options.Triggers=true;
                transfer.Options.ExtendedProperties=true;
                token.ThrowIfCancellationRequested();
                transfer.TransferData();
            },token);
        }
        catch (Exception) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); throw; }
        finally
        {
            sourceSmo.Disconnect();
            targetSmo.Disconnect();
        }

        var compatibility = Convert.ToInt32(info["compatibility_level"]);
        var rcsi = Convert.ToBoolean(info["is_read_committed_snapshot_on"]) ? "ON" : "OFF";
        var snapshot = Convert.ToInt32(info["snapshot_isolation_state"])==1 ? "ON" : "OFF";
        await Execute(control,$"""
            ALTER DATABASE {Q(stage)} SET COMPATIBILITY_LEVEL={compatibility};
            ALTER DATABASE {Q(stage)} SET READ_COMMITTED_SNAPSHOT {rcsi};
            ALTER DATABASE {Q(stage)} SET ALLOW_SNAPSHOT_ISOLATION {snapshot};
            """,token);
    }

    public static async Task<bool> TryRefreshLock(SqlConnection connection,string database,string mode,CancellationToken token)
    {
        var resource = L("DevRefresh.Publish."+database.ToLowerInvariant());
        return await Scalar<int>(connection,$"""
            DECLARE @r int;
            EXEC @r=sys.sp_getapplock @Resource={resource},@LockMode={L(mode)},@LockOwner='Session',@LockTimeout=0;
            SELECT @r;
            """,token)>=0;
    }

    public static async Task Publish(SqlConnection connection,string target,string stage,string previous,bool disconnect,
        Action register,CancellationToken token)
    {
        if (await Scalar<int>(connection,$"SELECT COUNT(*) FROM sys.databases WHERE name={L(previous)}",token)!=0)
            throw new InputException("Служебное имя предыдущей базы уже занято.");
        register();
        var access = disconnect ? "WITH ROLLBACK IMMEDIATE" : "WITH NO_WAIT";
        await Execute(connection,$"ALTER DATABASE {Q(stage)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE",token);
        await Execute(connection,$"ALTER DATABASE {Q(target)} SET SINGLE_USER {access}",token);
        await Execute(connection,$"ALTER DATABASE {Q(target)} MODIFY NAME={Q(previous)}",token);
        await Execute(connection,$"ALTER DATABASE {Q(stage)} MODIFY NAME={Q(target)}",token);
        await Execute(connection,$"ALTER DATABASE {Q(target)} SET MULTI_USER WITH NO_WAIT",token);
    }

    public static Task FinishPublish(SqlConnection connection,string target,string stage,string previous,CancellationToken token) =>
        Execute(connection,$"""
            IF DB_ID({L(previous)}) IS NOT NULL
            BEGIN
                IF DB_ID({L(target)}) IS NULL
                    ALTER DATABASE {Q(previous)} MODIFY NAME={Q(target)};
                ELSE IF DB_ID({L(stage)}) IS NOT NULL
                    THROW 50001, N'Не удалось однозначно определить результат замены базы назначения.', 1;
                ELSE
                    DROP DATABASE {Q(previous)};
            END;
            IF EXISTS(SELECT 1 FROM sys.databases WHERE name={L(target)} AND user_access=1)
                ALTER DATABASE {Q(target)} SET MULTI_USER WITH NO_WAIT;
            """,token);
}
