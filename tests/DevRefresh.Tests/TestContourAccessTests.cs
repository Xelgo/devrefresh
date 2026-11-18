using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DevRefresh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevRefresh.Tests;

public sealed class TestContourAccessTests
{
    sealed class Gateway : ISqlGateway
    {
        public Task<ConnectionResult> Test(SqlSettings settings,CancellationToken token) =>
            Task.FromResult(new ConnectionResult("TEST-SQL","test","test"));
        public Task<IReadOnlyList<SqlDatabaseInfo>> List(SqlSettings settings,CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SqlDatabaseInfo>>([
                new("uho_ke_tk","ONLINE"),new("bp_gm_TK","ONLINE"),
                new("uho_ke_dev_alice","ONLINE"),new("uho_ke_dev_mallory","ONLINE"),
                new("uho_ke_prod","ONLINE"),new("uho_ke_tk_extra","ONLINE"),new("master","ONLINE")]);
    }

    [Theory]
    [InlineData("uho_ke_tk","uho_ke","",true)]
    [InlineData("UHO_KE_TK","uho_ke","",true)]
    [InlineData("bp_tk","bp","",true)]
    [InlineData("uho_ke_dev_alice_tk","uho_ke","alice_tk",false)]
    public void ParsesSharedContourWithoutInventingAnOwner(string input,string source,string owner,bool testContour)
    {
        var parsed=Assert.IsType<DatabaseName>(Rules.ParseDatabase(input));
        Assert.Equal(source,parsed.SourceKey);
        Assert.Equal(owner,parsed.OwnerKey);
        Assert.Equal(testContour,parsed.IsTestContour);
        Assert.False(Rules.Owns("CORP\\tk","uho_ke_tk"));
    }

    [Theory]
    [InlineData("_tk")]
    [InlineData("uho__ke_tk")]
    [InlineData("uho.ke_tk")]
    [InlineData("uho_ke_tk_extra")]
    [InlineData("uho_ke_tk;drop")]
    public void RejectsMalformedContourNames(string name) => Assert.Null(Rules.ParseDatabase(name));

    [Fact]
    public void ContourDatabasesAreNotDeveloperCopySourcesOrTargets()
    {
        Assert.False(WorkflowRules.IsCopySource("uho_ke_tk","uho_ke_dev_alice","uho_ke"));
        Assert.False(WorkflowRules.IsCopySource("uho_ke_dev_alice","uho_ke_tk","uho_ke"));
        Assert.True(WorkflowRules.IsCopySource("uho_ke_dev_mallory","uho_ke_dev_alice","uho_ke"));
    }

    static async Task<HttpResponseMessage> Send(HttpClient client,HttpMethod method,string path,object payload)
    {
        var session=(await client.GetFromJsonAsync<JsonObject>("/api/session"))!;
        using var request=new HttpRequestMessage(method,path){Content=JsonContent.Create(payload)};
        request.Headers.Add("X-CSRF-TOKEN",session["csrfToken"]!.GetValue<string>());
        return await client.SendAsync(request);
    }

    static async Task<HttpClient> Prepare(Factory app)
    {
        var admin=app.Client();
        Assert.Equal(HttpStatusCode.OK,(await Send(admin,HttpMethod.Post,"/api/setup",
            new SetupInput("admin","Synthetic-test-contour-password!",[]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Send(admin,HttpMethod.Put,"/api/admin/settings",
            new SettingsInput("TEST-SQL","sql","service","Synthetic-password!",app.DirectoryPath,app.DirectoryPath,false))).StatusCode);
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,"uho.bak"),"Synthetic backup; never restored");
        Assert.Equal(HttpStatusCode.OK,(await Send(admin,HttpMethod.Put,"/api/admin/reduction/backup-aliases/uho_ke",
            new BackupAliasInput("uho"))).StatusCode);
        return admin;
    }

    static HttpClient Domain(Factory app,string account)
    {
        var client=app.Client();
        client.DefaultRequestHeaders.Add("Test-Domain",account);
        return client;
    }

    static Task<HttpResponseMessage> Access(HttpClient admin,string[] accounts) =>
        Send(admin,HttpMethod.Put,"/api/admin/security",new SecurityInput([],null,TestContourOperators:accounts));

    static async Task<JsonArray> Databases(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonObject>("/api/databases"))!["databases"]!.AsArray();

    [Fact]
    public async Task SharedListGrantsOnlyTkAccessAndUsesTheMatchingBackupAlias()
    {
        using var app=new Factory{SqlGateway=new Gateway()};using var admin=await Prepare(app);
        Assert.Equal(HttpStatusCode.OK,(await Access(admin,[" CORP\\alice ","corp\\ALICE","CORP\\bob"])).StatusCode);
        var settings=(await admin.GetFromJsonAsync<JsonObject>("/api/admin/settings"))!;
        Assert.Equal(new[]{"CORP\\alice","CORP\\bob"},settings["testContourOperators"]!.AsArray().Select(x=>x!.GetValue<string>()));
        using var alice=Domain(app,"corp\\ALICE");
        var rows=await Databases(alice);
        Assert.Equal(new[]{"uho_ke_tk","bp_gm_TK","uho_ke_dev_alice"},rows.Select(x=>x!["name"]!.GetValue<string>()));
        var tk=rows[0]!;
        Assert.True(tk["isTestContour"]!.GetValue<bool>());
        Assert.Equal("uho_ke",tk["sourceKey"]!.GetValue<string>());
        Assert.Equal("uho.bak",tk["backup"]!["name"]!.GetValue<string>());
        Assert.False((await alice.GetFromJsonAsync<JsonObject>("/api/session"))!["user"]!["isAdmin"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden,(await alice.GetAsync("/api/admin/settings")).StatusCode);
        using var mallory=Domain(app,"CORP\\mallory");
        Assert.Equal("uho_ke_dev_mallory",Assert.Single(await Databases(mallory))!["name"]!.GetValue<string>());
        using var anotherDomain=Domain(app,"OTHER\\alice");
        Assert.DoesNotContain(await Databases(anotherDomain),x=>x!["isTestContour"]!.GetValue<bool>());
        Assert.Equal(4,(await Databases(admin)).Count);
    }

    [Fact]
    public async Task PermissionIsEnforcedOnRefreshAndRevokedWithoutSigningInAgain()
    {
        using var app=new Factory{SqlGateway=new Gateway()};using var admin=await Prepare(app);
        using var alice=Domain(app,"CORP\\alice");
        var request=new{database="uho_ke_tk",confirmReplace=true,confirmDisconnect=true};
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(alice,HttpMethod.Post,"/api/refresh",request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Access(admin,["CORP\\alice"])).StatusCode);
        var accepted=await Send(alice,HttpMethod.Post,"/api/refresh",request);
        Assert.Equal(HttpStatusCode.Accepted,accepted.StatusCode);
        var id=(await accepted.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
        var job=app.Services.GetRequiredService<Store>().GetJob(id)!;
        Assert.Equal("uho_ke",job.SourceKey);
        Assert.True(job.Request.ResolveLatestBackup);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(alice,HttpMethod.Post,"/api/refresh",
            new{database="uho_ke_tk",confirmReplace=true,sourceDatabase="uho_ke_dev_alice"})).StatusCode);
        // An older settings client omitting the new property must not silently revoke access.
        Assert.Equal(HttpStatusCode.OK,(await Send(admin,HttpMethod.Put,"/api/admin/security",new SecurityInput([],null))).StatusCode);
        Assert.Contains(await Databases(alice),x=>x!["isTestContour"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK,(await Access(admin,[])).StatusCode);
        Assert.DoesNotContain(await Databases(alice),x=>x!["isTestContour"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(alice,HttpMethod.Post,"/api/refresh",request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await alice.GetAsync("/api/refresh/options?database=uho_ke_tk")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await alice.GetAsync("/api/jobs/"+id)).StatusCode);
        Assert.Empty((await alice.GetFromJsonAsync<JsonObject>("/api/jobs/dashboard"))!["jobs"]!.AsArray());
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("CORP\\*")]
    public async Task OnlyAdministratorsCanChangeAnExactAdAccountList(string invalid)
    {
        using var app=new Factory{SqlGateway=new Gateway()};using var admin=await Prepare(app);
        using var alice=Domain(app,"CORP\\alice");
        Assert.Equal(HttpStatusCode.Forbidden,(await Access(alice,["CORP\\alice"])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Access(admin,[invalid])).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<JsonObject>("/api/admin/settings"))!["testContourOperators"]!.AsArray());
    }

    [Fact]
    public async Task LegacySettingsHaveAnEmptyListAndTheListSurvivesRestart()
    {
        using var app=new Factory{SqlGateway=new Gateway()};using var admin=await Prepare(app);
        var store=app.Services.GetRequiredService<Store>();
        var configuration=app.Services.GetRequiredService<IConfiguration>();
        store.SaveSecurity([],null,"admin",new WindowsAccess(true,false));
        Assert.False(store.CanRefreshTestContour("CORP\\alice",configuration));
        Assert.Equal(HttpStatusCode.OK,(await Access(admin,["CORP\\alice"])).StatusCode);
        var reopened=new Store(Path.Combine(app.DirectoryPath,"devrefresh.sqlite"));
        Assert.True(reopened.CanRefreshTestContour("corp\\ALICE",configuration));
        Assert.False(reopened.CanRefreshTestContour("OTHER\\alice",configuration));
        Assert.True(reopened.CanRefreshTestContour("admin",configuration));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedQueuedRefreshFailsBeforeDoingDatabaseWork(bool dt)
    {
        using var app=new Factory{SqlGateway=new Gateway()};using var admin=await Prepare(app);
        Assert.Equal(HttpStatusCode.OK,(await Access(admin,["CORP\\alice"])).StatusCode);
        var store=app.Services.GetRequiredService<Store>();
        var job=store.CreateJob("refresh","CORP\\alice","uho_ke","uho_ke_tk",
            new(store.GetSettings()!,"",null,[],false,true,ResolveLatestBackup:true,UseDt:dt));
        Assert.Equal(HttpStatusCode.OK,(await Access(admin,[])).StatusCode);
        app.Services.GetRequiredService<IConfiguration>()["Workflows:Execute"]="true";
        using var worker=ActivatorUtilities.CreateInstance<WorkflowService>(app.Services);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while(store.GetJob(job.Id)!.Status!="Failed")await Task.Delay(30,timeout.Token);
            Assert.Contains("отозван",store.GetJob(job.Id)!.Error);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }
}
