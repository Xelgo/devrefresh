using System.Text.Json;
using DevRefresh;
using Xunit;

namespace DevRefresh.Tests;

public sealed class RestorePipelineMigrationTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"DevRefreshPipelineMigrations",Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyRulesMergeByAliasAndFormatAndConflictsAreDisabled(bool conflict)
    {
        var path=Path.Combine(root,"devrefresh.sqlite");
        DatabaseMigrator.Ensure(path,DatabaseMigrator.Migrations.Take(12).ToArray());
        using(var db=DatabaseMigrator.Connect(path))
        {
            using var cmd=db.CreateCommand();
            cmd.CommandText="INSERT INTO backup_aliases VALUES('bp_gm','bp'); INSERT INTO settings VALUES(1,'{\"Server\":\"SQL\",\"DtRestore\":{\"InfobaseUser\":\"unused\",\"ProtectedInfobasePassword\":\"old-ib-cipher\",\"SqlUsername\":\"dt_sql\",\"ProtectedSqlPassword\":\"keep-sql-cipher\"}}')";
            cmd.ExecuteNonQuery();
            void Insert(string database,string source,string domain)
            {
                cmd.CommandText="INSERT INTO restore_pipelines VALUES('SQL',$database,$source,$value)";cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$database",database);cmd.Parameters.AddWithValue("$source",source);
                cmd.Parameters.AddWithValue("$value",JsonSerializer.Serialize(new {Server="SQL",Database=database,SourceKind=source,
                    Operations=new[]{new RestoreOperation(RestorePipelineRules.WindowsDomain,domain)},UpdatedAt="old",UpdatedBy="admin",TargetDomain=domain}));
                cmd.ExecuteNonQuery();
            }
            Insert("bp_gm_dev_one","full","CORP");Insert("bp_gm_dev_other","shrink",conflict?"OTHER":"corp");
            Insert("bp_gm_tk","dt","DT_DOMAIN");
        }
        var store=new Store(path);Assert.Equal(2,store.GetRestorePipelines().Length);
        var bak=store.GetRestorePipeline("sql","BP","bak")!;
        if(conflict){Assert.Empty(bak.Operations);Assert.NotNull(bak.MigrationNotice);}
        else{Assert.Equal("CORP",Assert.Single(bak.Operations).TargetDomain);Assert.Null(bak.MigrationNotice);}
        Assert.Equal("DT_DOMAIN",Assert.Single(store.GetRestorePipeline("SQL","bp","dt")!.Operations).TargetDomain);
        using(var db=DatabaseMigrator.Connect(path))
        {
            using var cmd=db.CreateCommand();cmd.CommandText="SELECT value FROM settings";var json=(string)cmd.ExecuteScalar()!;
            Assert.DoesNotContain("Infobase",json);Assert.Contains("keep-sql-cipher",json);
        }
        store.DeleteRestorePipeline("SQL","bp","bak","admin");
        Assert.Null(new Store(path).GetRestorePipeline("SQL","bp","bak")); // do not reimport a deleted migrated rule
    }
    public void Dispose()
    {
        var allowed=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"DevRefreshPipelineMigrations"))+Path.DirectorySeparatorChar;
        if(!Path.GetFullPath(root).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unexpected fixture path.");
        if(Directory.Exists(root))Directory.Delete(root,true);
    }
}
