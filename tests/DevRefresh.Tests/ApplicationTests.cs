using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using DevRefresh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Connections.Features;
using Xunit;

namespace DevRefresh.Tests;

public sealed class TestSqlGateway : ISqlGateway
{
    public Task<ConnectionResult> Test(SqlSettings settings, CancellationToken token) =>
        Task.FromResult(new ConnectionResult("TEST-SQL", "test-version", "test-edition"));
    public Task<IReadOnlyList<SqlDatabaseInfo>> List(SqlSettings settings, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<SqlDatabaseInfo>>([
            new("bp_gm_dev_demo_owner", "ONLINE", 9126805504L), new("do_ke_dev_demo_owner", "ONLINE"),
            new("bp_gm_dev_demo_owner2", "ONLINE"), new("bp_gm_dev_other_owner_demo_owner", "ONLINE"),
            new("bp_gm_prod_demo_owner", "ONLINE"), new("master", "ONLINE")]);
}

// Test-only identity injection. This middleware is never registered by the application.
public sealed class TestConnectionItems : IConnectionItemsFeature
{
    private sealed class ItemsMap:Dictionary<object,object?>,IDictionary<object,object?>
    {
        object? IDictionary<object,object?>.this[object key]
        {get=>TryGetValue(key,out var value)?value:null;set=>base[key]=value;}
    }
    public IDictionary<object,object?> Items {get;set;}=new ItemsMap();
}
public sealed class TestConnection : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            context.Features.Set<IConnectionItemsFeature>(new TestConnectionItems());
            context.Connection.RemoteIpAddress = context.Request.Headers.ContainsKey("Test-Remote")
                ? IPAddress.Parse("198.51.100.7") : IPAddress.Loopback;
            if (context.Request.Headers.TryGetValue("Test-Domain", out var domain))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.Name, domain.ToString()), new Claim("kind", "domain")], "TestOnly"));
                if(context.Request.Headers.TryGetValue("Test-Sid",out var sid))
                    ((ClaimsIdentity)context.User.Identity!).AddClaim(new(ClaimTypes.PrimarySid,sid.ToString()));
            }
            await continuation(context);
        });
        next(app);
    };
}

public sealed class Factory : WebApplicationFactory<Program>
{
    public bool WindowsEnabled { get; init; } = true;
    public bool McpEnabled {get;init;}
    public bool SyntheticNegotiate {get;init;}
    public IRefreshInspector? RefreshInspector {get;init;}
    public ISqlGateway? SqlGateway { get; init; }
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "DevRefreshTests");
    public string DirectoryPath { get; } = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/DevRefresh"));
        builder.UseContentRoot(source);
        builder.UseSetting("DataDirectory", DirectoryPath);
        builder.UseSetting("WindowsAuthentication:Enabled", WindowsEnabled.ToString());
        builder.UseSetting("Workflows:Execute", "false");
        builder.UseSetting("Mcp:Enabled",McpEnabled.ToString());
        builder.UseSetting("Mcp:PublicUrl","https://localhost");
        builder.UseSetting("Mcp:AllowedRedirectUris:0","https://client.example.com/callback");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISqlGateway>();
            services.AddSingleton<ISqlGateway>(SqlGateway ?? new TestSqlGateway());
            services.AddSingleton<IStartupFilter, TestConnection>();
            if(RefreshInspector is not null){services.RemoveAll<IRefreshInspector>();services.AddSingleton(RefreshInspector);}
            if(SyntheticNegotiate)
            {
                services.AddTransient<SyntheticNegotiateHandler>();
                services.PostConfigure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options=>
                    options.Schemes.Single(x=>x.Name=="Negotiate").HandlerType=typeof(SyntheticNegotiateHandler));
            }
        });
    }
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Path.GetFullPath(DirectoryPath).StartsWith(Path.GetFullPath(TestRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}

public sealed class ApplicationTests
{
    private const string Password = "Synthetic-test-password-123!";
    private static async Task<JsonObject> Session(HttpClient client) => (await client.GetFromJsonAsync<JsonObject>("/api/session"))!;
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object body, bool csrf = true)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        if (csrf) request.Headers.Add("X-CSRF-TOKEN", (await Session(client))["csrfToken"]!.GetValue<string>());
        return await client.SendAsync(request);
    }
    private static async Task Setup(HttpClient client, string[]? accounts = null)
    {
        var response = await Send(client, HttpMethod.Post, "/api/setup", new SetupInput("admin", Password, accounts));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    private static SettingsInput Settings(string path, string? password = "SQL-synthetic-password!") =>
        new("TEST-SQL", "sql", "dev_service", password, path, path, false);

    [Theory]
    [InlineData("CORP\\demo_owner", "bp_gm_dev_demo_owner", true)]
    [InlineData("corp\\DEMO_OWNER", "do_ke_dev_Demo_Owner", true)]
    [InlineData("CORP\\demo_owner", "bp_gm_dev_demo_owner2", true)]
    [InlineData("CORP\\demo_owner", "bp_gm_dev_other_owner_demo_owner", true)]
    [InlineData("CORP\\demo_owner", "bp_gm_dev_edt_demo_owner", true)]
    [InlineData("CORP\\demo_owner", "bp_gm_dev_demo_owner_edt", true)]
    [InlineData("corp\\DEMO_OWNER", "BP_GM_DEV_EDT_Demo_Owner_COPY", true)]
    [InlineData("CORP\\demo_owner", "demo_owner_dev_other_owner", true)]
    [InlineData("CORP\\demo_owner", "bp_gm_dev_other_owner", false)]
    [InlineData("CORP\\demo_owner", "bp_gm_prod_demo_owner", false)]
    [InlineData("CORP\\demo_owner", "bp_dev_one_dev_demo_owner", false)]
    [InlineData("CORP\\demo_owner", "bp_gm_devedt_demo_owner", false)]
    [InlineData("CORP\\demo_owner", "demo_owner_tk", false)]
    [InlineData("demo_owner", "bp_gm_dev_demo_owner", false)]
    public void OwnershipMatchesAccountSubstringInAValidDeveloperDatabaseName(string identity, string database, bool expected)
    {
        Assert.Equal(expected, Rules.Owns(identity, database));
        var user = new AppUser(identity, "domain", false, Rules.OwnerFromIdentity(identity));
        Assert.Equal(expected, Rules.CanAccessDatabase(Rules.ParseDatabase(database), user));
    }

    [Fact]
    public async Task SetupIsOnceOnlyAndPasswordIsHashed()
    {
        using var app = new Factory();
        using var client = app.Client();
        Assert.True((await Session(client))["setupRequired"]!.GetValue<bool>());
        await Setup(client);
        Assert.Equal("admin", (await Session(client))["user"]!["name"]!.GetValue<string>());
        var duplicate = await Send(client, HttpMethod.Post, "/api/setup", new SetupInput("other", Password, []));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var stored = app.Services.GetRequiredService<Store>().GetAdmin()!;
        Assert.NotEqual(Password, stored.PasswordHash);
        Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(app.DirectoryPath, "devrefresh.sqlite")));
    }

    [Fact]
    public async Task SetupRequiresLoopbackEvenOverHttps()
    {
        using var app = new Factory();
        using var client = app.Client();
        client.DefaultRequestHeaders.Add("Test-Remote", "true");
        var response = await Send(client, HttpMethod.Post, "/api/setup", new SetupInput("admin", Password, []));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(app.Services.GetRequiredService<Store>().GetAdmin());
    }

    [Fact]
    public async Task MutationsRequireCsrfAndAdminEndpointsRequireAuthentication()
    {
        using var app = new Factory();
        using var client = app.Client();
        var missingCsrf = await Send(client, HttpMethod.Post, "/api/setup", new SetupInput("admin", Password, []), csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/settings")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Remote-User", "CORP\\administrator");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/settings")).StatusCode);
    }

    [Fact]
    public async Task WebInstallationRequiresAdminAndCsrfAndIsDisabledInConsoleMode()
    {
        using var app = new Factory();
        using var admin = app.Client();
        using var developer = app.Client();
        using var anonymous = app.Client();
        await Setup(admin);
        developer.DefaultRequestHeaders.Add("Test-Domain", "CORP\\demo_owner");
        const string route = "/api/admin/update";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.GetAsync(route + "/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(developer,HttpMethod.Post,route,new {version="99.0.0"})).StatusCode);
        var status=(await admin.GetFromJsonAsync<JsonObject>(route))!;
        Assert.False(string.IsNullOrEmpty(status["currentVersion"]?.GetValue<string>()));
        Assert.Contains("службы Windows",status["unavailable"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(admin,HttpMethod.Post,route,new {version="99.0.0"},csrf:false)).StatusCode);
        var blocked=await Send(admin,HttpMethod.Post,route,new {version="99.0.0"});
        Assert.Equal(HttpStatusCode.BadRequest,blocked.StatusCode);
        Assert.Contains("службы Windows",(await blocked.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task SqlPasswordIsEncryptedRetainedAndNeverReturned()
    {
        using var app = new Factory();
        using var client = app.Client();
        await Setup(client);
        var input = Settings(app.DirectoryPath);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Put, "/api/admin/settings", input)).StatusCode);
        var store = app.Services.GetRequiredService<Store>();
        var original = store.GetSettings()!;
        Assert.NotEqual(input.Password, original.ProtectedPassword);
        Assert.DoesNotContain(input.Password!, original.ProtectedPassword);
        var publicJson = await client.GetStringAsync("/api/admin/settings");
        Assert.DoesNotContain(input.Password!, publicJson);
        Assert.DoesNotContain(original.ProtectedPassword, publicJson);
        Assert.DoesNotContain(input.Password!, File.ReadAllText(Path.Combine(app.DirectoryPath, "devrefresh.sqlite")));
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Put, "/api/admin/settings", input with { Password = null })).StatusCode);
        Assert.Equal(original.ProtectedPassword, store.GetSettings()!.ProtectedPassword);
        var reloaded = new Store(Path.Combine(app.DirectoryPath, "devrefresh.sqlite"));
        Assert.Equal(original, reloaded.GetSettings());
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(app.Services.GetRequiredService<SettingsService>().ConnectionString(reloaded.GetSettings()!));
        Assert.Equal(input.Password, connection.Password);
        Assert.Equal("master", connection.InitialCatalog);
        Assert.False(connection.TrustServerCertificate);
        var changed = await Send(client, HttpMethod.Put, "/api/admin/settings", input with { Server = "OTHER-SQL", Password = null });
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        Assert.Equal("TEST-SQL", store.GetSettings()!.Server);
    }

    [Fact]
    public async Task ConnectionTestDoesNotSaveProposedSettings()
    {
        using var app = new Factory();
        using var client = app.Client();
        await Setup(client);
        var response = await Send(client, HttpMethod.Post, "/api/admin/connection/test", Settings(app.DirectoryPath));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(app.Services.GetRequiredService<Store>().GetSettings());
    }

    [Fact]
    public async Task DomainDeveloperSeesOwnDatabasesAndCannotAccessAdministration()
    {
        using var app = new Factory();
        using var admin = app.Client();
        await Setup(admin);
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath, "bp_gm.bak"), "Synthetic backup candidate, not a real SQL backup");
        await Send(admin, HttpMethod.Put, "/api/admin/settings", Settings(app.DirectoryPath));
        using var developer = app.Client();
        developer.DefaultRequestHeaders.Add("Test-Domain", "CORP\\demo_owner");
        var result = await developer.GetFromJsonAsync<JsonObject>("/api/databases");
        var rows = result!["databases"]!.AsArray();
        Assert.Equal(new[]{"bp_gm_dev_demo_owner", "do_ke_dev_demo_owner", "bp_gm_dev_demo_owner2", "bp_gm_dev_other_owner_demo_owner"},
            rows.Select(row => row!["name"]!.GetValue<string>()));
        Assert.Equal(9126805504L,rows[0]!["sizeBytes"]!.GetValue<long>());
        Assert.Null(rows[1]!["sizeBytes"]);
        Assert.Equal("other_owner_demo_owner", rows[3]!["ownerKey"]!.GetValue<string>());
        Assert.Equal("bp_gm.bak", rows[0]!["backup"]!["name"]!.GetValue<string>());
        Assert.Null(rows[1]!["backup"]);
        var refresh = await Send(developer, HttpMethod.Post, "/api/refresh",
            new { database = "bp_gm_dev_other_owner_demo_owner", confirmReplace = true });
        Assert.Equal(HttpStatusCode.Accepted, refresh.StatusCode);
        var jobId = (await refresh.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await developer.GetAsync("/api/jobs/" + jobId)).StatusCode);
        using var unrelated = app.Client();
        unrelated.DefaultRequestHeaders.Add("Test-Domain", "CORP\\unrelated_owner");
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(unrelated, HttpMethod.Post, "/api/refresh",
            new { database = "bp_gm_dev_other_owner_demo_owner", confirmReplace = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await unrelated.GetAsync("/api/jobs/" + jobId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.GetAsync("/api/admin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(developer, HttpMethod.Put, "/api/admin/settings", Settings(app.DirectoryPath))).StatusCode);
    }

    [Fact]
    public async Task DomainAllowlistIsExactAndRevocationTakesEffectImmediately()
    {
        using var app = new Factory();
        using var admin = app.Client();
        await Setup(admin, ["CORP\\demo_owner"]);
        using var domain = app.Client();
        domain.DefaultRequestHeaders.Add("Test-Domain", "corp\\DEMO_OWNER");
        Assert.Equal(HttpStatusCode.OK, (await domain.GetAsync("/api/admin/settings")).StatusCode);
        using var other = app.Client();
        other.DefaultRequestHeaders.Add("Test-Domain", "CORP\\demo_owner2");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/admin/settings")).StatusCode);
        await Send(admin, HttpMethod.Put, "/api/admin/security", new SecurityInput([], null));
        Assert.Equal(HttpStatusCode.Forbidden, (await domain.GetAsync("/api/admin/settings")).StatusCode);
    }

    [Fact]
    public async Task PasswordRotationRevokesOldCookiesAndOldPassword()
    {
        using var app = new Factory();
        using var first = app.Client();
        using var second = app.Client();
        await Setup(first);
        Assert.Equal(HttpStatusCode.OK, (await Send(second, HttpMethod.Post, "/api/login", new LoginInput("admin", Password))).StatusCode);
        var updated = await Send(first, HttpMethod.Put, "/api/admin/security", new SecurityInput([], "Replacement-test-password!"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/admin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/admin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(second, HttpMethod.Post, "/api/login", new LoginInput("admin", Password))).StatusCode);
    }

    [Fact]
    public async Task RepeatedBadLoginsAreRateLimited()
    {
        using var app = new Factory();
        using var client = app.Client();
        await Setup(client);
        var responses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) responses.Add((await Send(client, HttpMethod.Post, "/api/login", new LoginInput("admin", "incorrect"))).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, responses);
    }

    [Fact]
    public async Task RealSqlDriverReportsConnectionFailureWithoutLeakingCredentials()
    {
        using var app = new Factory();
        using var client = app.Client();
        await Setup(client);
        var settingsService = app.Services.GetRequiredService<SettingsService>();
        var input = Settings(app.DirectoryPath) with { Server = "tcp:127.0.0.1,1" };
        var gateway = new SqlGateway(settingsService);
        var error = await Assert.ThrowsAsync<SqlServiceException>(() => gateway.Test(settingsService.Prepare(input), CancellationToken.None));
        Assert.DoesNotContain(input.Password!, error.Message);
        Assert.NotNull(error.Number);
    }

    [Fact]
    public async Task LogRetentionPrunesOnlyExpiredClosedLogsAndCanBeSavedWithoutSqlSettings()
    {
        using var app = new Factory();
        var databasePath = Path.Combine(app.DirectoryPath, "devrefresh.sqlite");
        var store = new Store(databasePath);
        Assert.Equal(14, store.GetLogRetentionDays());
        store.SaveLogRetentionDays(3, "test");
        var logs = Path.Combine(app.DirectoryPath, "logs");
        Directory.CreateDirectory(logs);
        string WriteLog(string name, int daysOld)
        {
            var path = Path.Combine(logs, name);
            File.WriteAllText(path, "fixture");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
            return path;
        }

        var expired = WriteLog("devrefresh-20261010.jsonl", 5);
        var rotated = WriteLog("devrefresh-20261010_001.jsonl", 5);
        var fresh = WriteLog("devrefresh-20261011.jsonl", 2);
        var foreign = WriteLog("devrefresh-imported.jsonl", 20);
        var launcher = WriteLog("launcher.log", 20);
        var locked = WriteLog("devrefresh-20261010_002.jsonl", 5);
        using var writer = File.Open(locked, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        using var client = app.Client();
        for (var i = 0; i < 100 && (File.Exists(expired) || File.Exists(rotated)); i++)
            await Task.Delay(30);
        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(rotated));
        Assert.All(new[] { fresh, foreign, launcher, locked }, path => Assert.True(File.Exists(path)));

        await Setup(client);
        var settings = (await client.GetFromJsonAsync<JsonObject>("/api/admin/settings"))!;
        Assert.Equal(3, settings["logRetentionDays"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Send(client, HttpMethod.Put, "/api/admin/log-retention", new LogRetentionInput(0))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Send(client, HttpMethod.Put, "/api/admin/log-retention", new LogRetentionInput(30))).StatusCode);
        Assert.Equal(30, new Store(databasePath).GetLogRetentionDays());
        Assert.Null(store.GetSettings());
    }

    [Fact]
    public async Task FileLogsCorrelateRequestsWithoutRecordingSubmittedPasswords()
    {
        using var app = new Factory();
        using var client = app.Client();
        await Setup(client);
        var input = Settings(app.DirectoryPath);
        await Send(client, HttpMethod.Put, "/api/admin/settings", input);
        var health = await client.GetAsync("/health");
        var requestId = Assert.Single(health.Headers.GetValues("X-Request-ID"));
        var log = "";
        // Request completion can be logged after TestServer has delivered the response body.
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var messages = new List<string>();
            foreach (var path in Directory.GetFiles(Path.Combine(app.DirectoryPath, "logs"), "*.jsonl"))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                messages.Add(reader.ReadToEnd());
            }
            log = string.Join("\n", messages);
            if (log.Contains(requestId, StringComparison.Ordinal)) break;
            await Task.Delay(20);
        }
        Assert.Contains(requestId, log);
        Assert.Contains("/health", log);
        Assert.DoesNotContain(input.Password!, log);
        Assert.DoesNotContain(Password, log);
    }

    [Theory]
    [InlineData("invalid", "LocalMachine")]
    [InlineData("0000000000000000000000000000000000000000", "UnknownStore")]
    public void DeploymentRejectsInvalidCertificateSelection(string thumbprint, string store)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HttpsCertificate:Thumbprint"] = thumbprint,
            ["HttpsCertificate:StoreLocation"] = store
        }).Build();
        Assert.Throws<InvalidOperationException>(() => DeploymentCertificate.Load(config));
    }

    [Fact]
    public async Task RemotePlainHttpIsRejectedAndWindowsModeDoesNotStoreSqlPassword()
    {
        using var app = new Factory();
        using var client = app.Client();
        await Setup(client);
        var input = Settings(app.DirectoryPath) with { Authentication = "windows", Username = "ignored", Password = null };
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Put, "/api/admin/settings", input)).StatusCode);
        Assert.Empty(app.Services.GetRequiredService<Store>().GetSettings()!.ProtectedPassword);
        using var remote = app.Client();
        remote.BaseAddress = new Uri("http://localhost");
        remote.DefaultRequestHeaders.Add("Test-Remote", "true");
        Assert.Equal(HttpStatusCode.BadRequest, (await remote.GetAsync("/api/session")).StatusCode);
    }
}
