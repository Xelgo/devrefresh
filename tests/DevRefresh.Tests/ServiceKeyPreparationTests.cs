using System.Text.Json;
using System.Runtime.Versioning;
using System.Xml.Linq;
using DevRefresh;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevRefresh.Tests;

public sealed class ServiceKeyPreparationTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"DevRefreshServiceTests",Guid.NewGuid().ToString("N"));
    private string Keys=>Path.Combine(root,"keys");
    [SupportedOSPlatform("windows")]
    private IDataProtector Protector()=>DataProtectionProvider.Create(new DirectoryInfo(Keys),
        options=>options.SetApplicationName("DevRefresh").ProtectKeysWithDpapi()).CreateProtector("DevRefresh.SqlPassword.v1");

    [Fact]
    public void RewrapPreservesCiphertextKeyIdsAndExistingSettingsAndIsIdempotent()
    {
        if(!OperatingSystem.IsWindows())return;
        var ciphertext=Protector().Protect("synthetic-sql-password");
        var database=Path.Combine(root,"devrefresh.sqlite");
        DatabaseMigrator.Ensure(database);
        var json=JsonSerializer.Serialize(new SqlSettings("existing-sql","Sql","existing-user",ciphertext,"backups","sql-backups",false));
        using(var db=DatabaseMigrator.Connect(database))
        {
            using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO settings VALUES(1,$value)";
            cmd.Parameters.AddWithValue("$value",json);cmd.ExecuteNonQuery();
        }
        var file=Assert.Single(Directory.GetFiles(Keys,"*.xml"));
        var before=XDocument.Load(file);var keyId=(string?)before.Root!.Attribute("id");
        ServiceKeyPreparation.Prepare(root);
        Assert.True(File.Exists(Path.Combine(root,ServiceKeyPreparation.Marker)));
        var after=XDocument.Load(file);
        Assert.Equal(keyId,(string?)after.Root!.Attribute("id"));
        Assert.NotEqual(before.ToString(),after.ToString());
        Assert.Contains(after.Descendants(),e=>e.Name.LocalName=="encryptedSecret");
        Assert.Empty(after.Descendants("masterKey"));
        Assert.Equal("synthetic-sql-password",Protector().Unprotect(ciphertext));
        using(var db=DatabaseMigrator.Connect(database,SqliteOpenMode.ReadOnly))
        {
            using var cmd=db.CreateCommand();cmd.CommandText="SELECT value FROM settings";
            Assert.Equal(json,cmd.ExecuteScalar());
        }
        var once=File.ReadAllText(file);
        ServiceKeyPreparation.Prepare(root);
        Assert.Equal(once,File.ReadAllText(file));
    }

    [Fact]
    public void CorruptKeyLeavesAllOriginalFilesUntouched()
    {
        if(!OperatingSystem.IsWindows())return;
        _=Protector().Protect("fixture");
        var file=Assert.Single(Directory.GetFiles(Keys,"*.xml"));var original=File.ReadAllText(file);
        File.WriteAllText(Path.Combine(Keys,"invalid.xml"),"<invalid");
        Assert.Throws<InvalidOperationException>(()=>ServiceKeyPreparation.Prepare(root));
        Assert.Equal(original,File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(root,ServiceKeyPreparation.Marker)));
    }

    [Fact]
    public void WrongSqlSecretRollsBackRewrappedKeys()
    {
        if(!OperatingSystem.IsWindows())return;
        _=Protector().Protect("fixture");
        var database=Path.Combine(root,"devrefresh.sqlite");DatabaseMigrator.Ensure(database);
        using(var db=DatabaseMigrator.Connect(database))
        {
            using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO settings VALUES(1,$value)";
            cmd.Parameters.AddWithValue("$value",JsonSerializer.Serialize(new SqlSettings("sql","Sql","user","invalid-ciphertext","backups","backups",false)));
            cmd.ExecuteNonQuery();
        }
        var file=Assert.Single(Directory.GetFiles(Keys,"*.xml"));var original=File.ReadAllText(file);
        Assert.ThrowsAny<Exception>(()=>ServiceKeyPreparation.Prepare(root));
        Assert.Equal(original,File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(root,ServiceKeyPreparation.Marker)));
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var parent=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"DevRefreshServiceTests"))+Path.DirectorySeparatorChar;
        if(Directory.Exists(root)&&Path.GetFullPath(root).StartsWith(parent,StringComparison.OrdinalIgnoreCase))Directory.Delete(root,true);
    }
}
