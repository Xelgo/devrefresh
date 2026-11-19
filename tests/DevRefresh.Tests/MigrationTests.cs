using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DevRefresh;
using Xunit;

namespace DevRefresh.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "DevRefreshMigrationTests", Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(root, "data", "devrefresh.sqlite");
    private void Legacy()
    {
        DatabaseMigrator.Ensure(Database, DatabaseMigrator.Migrations.Take(1).ToArray());
        using var db = DatabaseMigrator.Connect(Database);
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO local_admin VALUES(1,'existing_admin','existing_password_hash','existing-session-version');
            INSERT INTO windows_admins VALUES('CORP\demo_owner');
            INSERT INTO settings VALUES(1,'{"Server":"EXISTING-SQL","ProtectedPassword":"existing-ciphertext"}');
            INSERT INTO audit(at,actor,action,detail) VALUES('2026-10-27','existing_admin','previous.event','preserve me');
            """;
        command.ExecuteNonQuery();
    }
    private static string Scalar(string path, string sql)
    {
        using var db = DatabaseMigrator.Connect(path);
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!.ToString()!;
    }
    [Fact]
    public void UpgradePreservesSettingsCredentialsAndCreatesOneBackup()
    {
        Legacy();
        var settings = Scalar(Database, "SELECT value FROM settings");
        var result = DatabaseMigrator.Ensure(Database);
        Assert.Equal(1, result.FromVersion);
        Assert.Equal(DatabaseMigrator.LatestVersion, result.ToVersion);
        Assert.NotNull(result.BackupPath);
        Assert.Equal(1, DatabaseMigrator.ReadVersion(result.BackupPath));
        Assert.Equal(settings, Scalar(Database, "SELECT value FROM settings"));
        Assert.Equal("existing_password_hash", Scalar(Database, "SELECT password_hash FROM local_admin"));
        Assert.Equal("CORP\\demo_owner", Scalar(Database, "SELECT name FROM windows_admins"));
        Assert.Equal("preserve me", Scalar(Database, "SELECT detail FROM audit"));
        Assert.Equal(DatabaseMigrator.LatestVersion.ToString(), Scalar(Database, "SELECT COUNT(*) FROM schema_migrations"));
        Assert.Null(DatabaseMigrator.Ensure(Database).BackupPath);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(result.BackupPath)!, "*.sqlite"));
    }
    [Fact]
    public void FailedMigrationRollsBackDdlDataAndVersion()
    {
        Legacy();
        DatabaseMigrator.Ensure(Database);
        var migrations = DatabaseMigrator.Migrations.Append(new SchemaMigration(DatabaseMigrator.LatestVersion+1, "Failure test",
            "CREATE TABLE must_rollback(id INTEGER); UPDATE settings SET value='changed'; INSERT INTO missing_table VALUES(1);")).ToArray();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => DatabaseMigrator.Ensure(Database, migrations));
        Assert.Equal(DatabaseMigrator.LatestVersion, DatabaseMigrator.ReadVersion(Database));
        Assert.Equal("0", Scalar(Database, "SELECT COUNT(*) FROM sqlite_master WHERE name='must_rollback'"));
        Assert.Contains("existing-ciphertext", Scalar(Database, "SELECT value FROM settings"));
        Assert.Equal(DatabaseMigrator.LatestVersion.ToString(), Scalar(Database, "SELECT COUNT(*) FROM schema_migrations"));
    }
    [Fact]
    public void FutureSchemaIsRejectedWithoutResettingItsVersion()
    {
        Legacy();
        Scalar(Database, "PRAGMA user_version=999; SELECT 'ok'");
        Assert.Throws<InvalidOperationException>(() => DatabaseMigrator.Ensure(Database));
        Assert.Equal(999, DatabaseMigrator.ReadVersion(Database));
        Assert.Contains("existing-ciphertext", Scalar(Database, "SELECT value FROM settings"));
    }
    [Fact]
    public void BackupIncludesCommittedWalWhileWriterConnectionRemainsOpen()
    {
        Legacy();
        using var db = DatabaseMigrator.Connect(Database);
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA wal_autocheckpoint=0; INSERT INTO windows_admins VALUES('CORP\\wal_user')";
        command.ExecuteNonQuery();
        Assert.True(new FileInfo(Database + "-wal").Length > 0);
        var snapshot = Path.Combine(root, "snapshot.sqlite");
        DatabaseMigrator.Backup(Database, snapshot);
        Assert.Equal("2", Scalar(snapshot, "SELECT COUNT(*) FROM windows_admins"));
    }
    [Fact]
    public void DataLockPreventsConcurrentRuntimeAndMaintenance()
    {
        var data = Path.Combine(root, "data");
        using (RuntimeDataLock.Acquire(data)) Assert.Throws<InvalidOperationException>(() => RuntimeDataLock.Acquire(data));
        using var reopened = RuntimeDataLock.Acquire(data);
    }
    private (string Package, string Target, string Data) UpdateFixture(bool forceFailure = false)
    {
        Legacy();
        var target = Path.Combine(root, "installed");
        var package = Path.Combine(root, "new-package");
        Directory.CreateDirectory(Path.Combine(target, "app"));
        Directory.CreateDirectory(Path.Combine(target, "config"));
        Directory.CreateDirectory(Path.Combine(package, "app"));
        Directory.CreateDirectory(Path.Combine(root, "data", "keys"));
        File.WriteAllText(Path.Combine(root, "data", "keys", "existing.xml"), "existing-protected-key");
        File.WriteAllText(Path.Combine(target, "app", "DevRefresh.exe"), "old executable placeholder");
        File.WriteAllText(Path.Combine(target, "config", "server.json"), "preserve existing server config");
        File.WriteAllText(Path.Combine(package, "app", "DevRefresh.exe"), "new executable placeholder");
        var files = new Dictionary<string, string> { ["app/DevRefresh.exe"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(package, "app", "DevRefresh.exe")))) };
        if (forceFailure)
        {
            File.WriteAllText(Path.Combine(package, "README.md"), "new documentation");
            files["README.md"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(package, "README.md"))));
            Directory.CreateDirectory(Path.Combine(target, "README.md"));
        }
        File.WriteAllText(Path.Combine(package, "BUILD-INFO.json"), JsonSerializer.Serialize(new PackageManifest("DevRefresh", "9.9.9", DatabaseMigrator.LatestVersion, files)));
        return (package, target, Path.Combine(root, "data"));
    }
    [Fact]
    public void UpdatePreservesExternalDataKeysAndServerConfiguration()
    {
        var fixture = UpdateFixture();
        var result = UpdateEngine.Apply(fixture.Package, fixture.Target, fixture.Data);
        Assert.Equal("installed-awaiting-start", result.State);
        Assert.Equal("new executable placeholder", File.ReadAllText(Path.Combine(fixture.Target, "app", "DevRefresh.exe")));
        Assert.Equal("preserve existing server config", File.ReadAllText(Path.Combine(fixture.Target, "config", "server.json")));
        Assert.Equal("existing-protected-key", File.ReadAllText(Path.Combine(fixture.Data, "keys", "existing.xml")));
        Assert.Equal(DatabaseMigrator.LatestVersion, DatabaseMigrator.ReadVersion(Database));
        Assert.Equal(1, DatabaseMigrator.ReadVersion(Path.Combine(result.BackupDirectory, "database.sqlite")));
    }
    [Fact]
    public void UpdateBacksUpOAuthStateWithoutChangingItsLiveRevocations()
    {
        var fixture=UpdateFixture();var oauth=Path.Combine(fixture.Data,"mcp-oauth.sqlite");
        using(var db=DatabaseMigrator.Connect(oauth))
        {
            using var command=db.CreateCommand();command.CommandText="CREATE TABLE grants(state TEXT); INSERT INTO grants VALUES('revoked')";command.ExecuteNonQuery();
        }
        File.WriteAllText(Path.Combine(fixture.Data,"mcp-keys.protected"),"synthetic-protected-key-fixture");
        var result=UpdateEngine.Apply(fixture.Package,fixture.Target,fixture.Data);
        Assert.Equal("revoked",Scalar(Path.Combine(result.BackupDirectory,"mcp-oauth.sqlite"),"SELECT state FROM grants"));
        Assert.Equal("revoked",Scalar(oauth,"SELECT state FROM grants"));
        Assert.Equal("synthetic-protected-key-fixture",File.ReadAllText(Path.Combine(result.BackupDirectory,"mcp-keys.protected")));
    }
    [Fact]
    public void ServiceUpdateBackupKeepsPrivateMasterKeyAccess()
    {
        if(!OperatingSystem.IsWindows())return;
        var fixture=UpdateFixture();
        var access=new DirectorySecurity();access.SetAccessRuleProtection(true,false);
        access.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(fixture.Data).SetAccessControl(access);
        File.WriteAllText(Path.Combine(fixture.Data,ServiceKeyPreparation.Marker),"fixture");
        var result=UpdateEngine.Apply(fixture.Package,fixture.Target,fixture.Data);
        var saved=new DirectoryInfo(result.BackupDirectory).GetAccessControl(AccessControlSections.Access);
        Assert.True(saved.AreAccessRulesProtected);
        var key=new FileInfo(Path.Combine(result.BackupDirectory,"keys","existing.xml")).GetAccessControl(AccessControlSections.Access);
        var rules=key.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.Single(rules);
        Assert.Equal(WindowsIdentity.GetCurrent().User,rules[0].IdentityReference);
    }
    [Fact]
    public void FailureAfterSchemaMigrationRestoresOldApplicationAndOldDatabase()
    {
        var fixture = UpdateFixture(forceFailure: true);
        Assert.Throws<InvalidOperationException>(() => UpdateEngine.Apply(fixture.Package, fixture.Target, fixture.Data));
        Assert.Equal("old executable placeholder", File.ReadAllText(Path.Combine(fixture.Target, "app", "DevRefresh.exe")));
        Assert.Equal(1, DatabaseMigrator.ReadVersion(Database));
        Assert.Contains("existing-ciphertext", Scalar(Database, "SELECT value FROM settings"));
        Assert.Equal("existing_password_hash", Scalar(Database, "SELECT password_hash FROM local_admin"));
    }
    [Fact]
    public void CorruptUpdatePayloadIsRejectedBeforeChangingAnything()
    {
        var fixture = UpdateFixture();
        File.AppendAllText(Path.Combine(fixture.Package, "app", "DevRefresh.exe"), "tampered");
        Assert.Throws<InvalidOperationException>(() => UpdateEngine.Apply(fixture.Package, fixture.Target, fixture.Data));
        Assert.Equal(1, DatabaseMigrator.ReadVersion(Database));
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, "backups")));
    }
    [Theory]
    [InlineData("MCP-OAUTH.md",true)]
    [InlineData("unexpected-root-file.txt",false)]
    [InlineData("config/credentials.json",false)]
    public void UpdateAcceptsTheMcpGuideAndStillRejectsUnapprovedPayloadFiles(string relative,bool allowed)
    {
        var fixture=UpdateFixture();
        var file=Path.Combine(fixture.Package,relative);Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file,"Synthetic package documentation fixture");
        var manifestPath=Path.Combine(fixture.Package,"BUILD-INFO.json");
        var manifest=JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(manifestPath))!;
        manifest.Files![relative]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        File.WriteAllText(manifestPath,JsonSerializer.Serialize(manifest));
        if(allowed)
        {
            Assert.Equal(manifest.Version,UpdateEngine.ValidatePackage(fixture.Package).Version);
            var result=UpdateEngine.Apply(fixture.Package,fixture.Target,fixture.Data);
            Assert.Equal("installed-awaiting-start",result.State);
            Assert.Equal(File.ReadAllText(file),File.ReadAllText(Path.Combine(fixture.Target,relative)));
        }
        else
        {
            Assert.Contains(relative,Assert.Throws<InvalidOperationException>(()=>UpdateEngine.ValidatePackage(fixture.Package)).Message);
            Assert.Throws<InvalidOperationException>(()=>UpdateEngine.Apply(fixture.Package,fixture.Target,fixture.Data));
            Assert.False(Directory.Exists(Path.Combine(fixture.Target,"backups")));
            Assert.Equal("old executable placeholder",File.ReadAllText(Path.Combine(fixture.Target,"app","DevRefresh.exe")));
        }
    }
    public void Dispose()
    {
        var allowed = Path.Combine(Path.GetTempPath(), "DevRefreshMigrationTests") + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
