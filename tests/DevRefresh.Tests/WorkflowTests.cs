using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevRefresh;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevRefresh.Tests;

public sealed class WorkflowTests
{
    static AnalyzedTable Table(string key,string? period)=>new(key,key,"dbo","_InfoRg1","РегистрСведений",key,"Main",1,true,null,period,null,null,2000,[],[],["_Key"],100,1,1,2,2);
    static AnalysisData Analysis(string id)=>new(id,"bp_gm","bp_gm.bak","Test","1",2000,"schema",
        [Table("РегистрСведений.Периодный","_Period") with{PeriodDistribution=new(100,[new("2025-01-01",50),new("2026-01-01",50)])},
        Table("РегистрСведений.Полный",null),Table("РегистрСведений.Другой",null)],[],DateTimeOffset.UtcNow.ToString("O"));
    [Theory]
    [InlineData("ut_ke.bak","ut_ke",null)]
    [InlineData("UT_KE.BAK","ut_ke",null)]
    [InlineData("BP_MKO_11-11-2026.BAK","bp_mko","2026-11-11")]
    [InlineData("do_01-12-2026.bak","do","2026-12-01")]
    public void AnalysisTypeComesFromBackupFilename(string filename,string expected,string? date)
    {
        var parsed=WorkflowRules.ParseBackupName(filename);
        Assert.NotNull(parsed);
        Assert.Equal(expected,parsed.Value.SourceKey);
        Assert.Equal(date,parsed.Value.BackupDate?.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture));
    }
    [Theory]
    [InlineData("../ut_ke.bak")]
    [InlineData("ut_ke.zip")]
    [InlineData("ut.ke.bak")]
    [InlineData("bp_mko_31-11-2026.bak")]
    [InlineData("bp_mko_1-11-2026.bak")]
    [InlineData("bp_mko_11-11-2026.bak.partial")]
    public void InvalidBackupNamesCannotBecomeAnalysisTypes(string filename)
        =>Assert.Null(WorkflowRules.ParseBackupName(filename));
    [Fact]
    public async Task NewBackupTypeIsListedAndStaleClientTypeCannotMislabelAnalysis()
    {
        using var app=new Factory();var(admin,_)=await Prepare(app);using var local=admin;
        const string selected="BP_MKO_11-11-2026.BAK";
        const string latest="bp_mko_01-12-2026.bak";
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,selected),"Synthetic older backup");
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,latest),"Synthetic newer backup");
        var catalog=(await local.GetFromJsonAsync<JsonObject>("/api/admin/reduction/catalog"))!;
        Assert.Contains("bp_mko",catalog["types"]!.AsArray().Select(x=>x!.GetValue<string>()));
        var backups=catalog["backups"]!.AsArray();
        Assert.Equal(new[]{latest,selected},backups.Select(x=>x!["name"]!.GetValue<string>()));
        Assert.All(backups,x=>Assert.Equal("bp_mko",x!["sourceKey"]!.GetValue<string>()));
        var response=await Send(local,HttpMethod.Post,"/api/admin/reduction/analyses",new{sourceKey="bp_gm",backupName=selected});
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var result=(await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("bp_mko",result["sourceKey"]!.GetValue<string>());
        var job=app.Services.GetRequiredService<Store>().GetJob(result["id"]!.GetValue<string>())!;
        Assert.Equal(selected,job.Request.BackupName);
    }
    [Fact]
    public void PeriodBoundaryIsLogicalAndMissingDateNeverMeansFullClear()
    {
        var analysis=Analysis(Guid.NewGuid().ToString("N"));
        Assert.Equal(new DateTime(4026,1,1),WorkflowRules.SqlDate("2026-01-01",2000));
        Assert.Throws<InputException>(()=>WorkflowRules.ValidateSelection(analysis,[new("РегистрСведений.Периодный",null)]));
        Assert.Throws<InputException>(()=>WorkflowRules.ValidateSelection(analysis,[new("РегистрСведений.Периодный","")]));
        Assert.Throws<InputException>(()=>WorkflowRules.ValidateSelection(analysis,[new("РегистрСведений.Полный","2026-01-01")]));
        Assert.Single(WorkflowRules.ValidateSelection(analysis,[new("РегистрСведений.Полный",null)]));
    }
    [Fact]
    public void DeveloperSelectionMustBeSubsetOfAdministrativeRules()
    {
        var analysis=Analysis(Guid.NewGuid().ToString("N"));var allowed=new HashSet<string>{"РегистрСведений.Периодный"};
        Assert.Throws<InputException>(()=>WorkflowRules.ValidateSelection(analysis,[new("РегистрСведений.Другой",null)],allowed));
        Assert.Throws<InputException>(()=>WorkflowRules.ValidateSelection(analysis,[new("РегистрСведений.Периодный","2026-01-01"),new("РегистрСведений.Периодный","2025-01-01")]));
        Assert.Empty(WorkflowRules.ValidateSelection(analysis,[],allowed));
    }
    static async Task<HttpResponseMessage> Send(HttpClient client,HttpMethod method,string path,object payload)
    {
        var session=(await client.GetFromJsonAsync<JsonObject>("/api/session"))!;
        using var request=new HttpRequestMessage(method,path){Content=JsonContent.Create(payload)};
        request.Headers.Add("X-CSRF-TOKEN",session["csrfToken"]!.GetValue<string>());
        return await client.SendAsync(request);
    }
    static async Task<(HttpClient Admin,ReductionProfile Profile)> Prepare(Factory app)
    {
        var client=app.Client();
        Assert.Equal(HttpStatusCode.OK,(await Send(client,HttpMethod.Post,"/api/setup",new SetupInput("admin","Synthetic-workflow-password-123!",[]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Send(client,HttpMethod.Put,"/api/admin/settings",new SettingsInput("TEST-SQL","sql","service","Synthetic-sql-password!",app.DirectoryPath,app.DirectoryPath,false))).StatusCode);
        var store=app.Services.GetRequiredService<Store>();
        var job=store.CreateJob("analysis","admin","bp_gm",null,new(store.GetSettings()!,"bp_gm.bak",null,[],false,false));
        Directory.CreateDirectory(Path.Combine(app.DirectoryPath,"analyses"));
        var legacyReport=JsonSerializer.SerializeToNode(Analysis(job.Id))!.AsObject();
        legacyReport["BackupSha256"]="legacy-hash";
        File.WriteAllText(Path.Combine(app.DirectoryPath,"analyses",job.Id+".json"),legacyReport.ToJsonString());
        store.UpdateJob(job.Id,"Succeeded","готово",new{AnalysisId=job.Id});
        var response=await Send(client,HttpMethod.Put,"/api/admin/reduction/profiles/bp_gm",new ProfileInput(job.Id,[new("РегистрСведений.Периодный","2026-01-01"),new("РегистрСведений.Полный",null)]));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        return(client,store.GetProfile("bp_gm")!);
    }
    [Fact]
    public async Task ManualShrinkBuildQueuesOnceWithoutCreatingASchedule()
    {
        using var app=new Factory();
        var (admin,profile)=await Prepare(app);
        using var local=admin;
        using var developer=app.Client();
        developer.DefaultRequestHeaders.Add("Test-Domain","CORP\\demo_owner");
        const string route="/api/admin/reduction/shrink-builds/bp_gm";
        Assert.Equal(HttpStatusCode.Forbidden,(await Send(developer,HttpMethod.Post,route,new{})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(local,HttpMethod.Post,route,new{})).StatusCode);

        var directory=Path.Combine(app.DirectoryPath,"trimmed");
        Directory.CreateDirectory(directory);
        var settings=new SettingsInput("TEST-SQL","sql","service",null,app.DirectoryPath,app.DirectoryPath,false,
            ShrinkBackupDirectory:directory,SqlShrinkBackupDirectory:directory);
        Assert.Equal(HttpStatusCode.OK,(await Send(local,HttpMethod.Put,"/api/admin/settings",settings)).StatusCode);
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,"bp_gm.bak"),"Synthetic backup");

        var responses=await Task.WhenAll(Send(local,HttpMethod.Post,route,new{}),Send(local,HttpMethod.Post,route,new{}));
        Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.Accepted);
        Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.BadRequest);
        var store=app.Services.GetRequiredService<Store>();
        var job=Assert.Single(store.GetQueued(),j=>j.Kind=="shrink-build");
        Assert.Equal("admin",job.Actor);
        Assert.Equal(profile.Revision,job.Request.Profile?.Revision);
        Assert.Empty(job.Request.BackupName);
        Assert.Null(job.Request.BuildSlot);
        Assert.Empty(store.GetShrinkSchedules());
    }

    [Fact]
    public void BackupSourceAllowsSqlReadersAndBlocksWritersUntilDisposed()
    {
        using var app=new Factory();
        var restore=app.Services.GetRequiredService<RestoreSql>();
        const string name="bp_11-11-2026.bak";
        var path=Path.Combine(app.DirectoryPath,name);
        File.WriteAllText(path,"Backup fixture");
        var sqlFolder=Path.Combine(app.DirectoryPath,"sql-backups");
        var settings=new SqlSettings("TEST-SQL","windows","","",app.DirectoryPath,sqlFolder,false);
        using(var source=restore.OpenBackupSource(settings,name,CancellationToken.None))
        {
            Assert.Equal(Path.Combine(sqlFolder,name),source.SqlPath);
            using var reader=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            Assert.Throws<IOException>(()=>File.OpenWrite(path).Dispose());
        }
        using var writer=File.OpenWrite(path);
    }
    [Fact]
    public async Task BackupAliasSelectsLatestSourceAndSavesReductionProfileForDevType()
    {
        using var app=new Factory();var(admin,_)=await Prepare(app);using var local=admin;
        using var developer=app.Client();developer.DefaultRequestHeaders.Add("Test-Domain","CORP\\demo_owner");
        const string aliasRoute="/api/admin/reduction/backup-aliases/DO_KE";
        const string database="do_ke_dev_demo_owner";
        const string older="ut_30-11-2026.bak";
        const string latest="ut_01-12-2026.bak";
        foreach(var name in new[]{latest,older,"do_ke.bak","other_31-12-2026.bak"})
            await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,name),"Synthetic backup");

        Assert.Equal(HttpStatusCode.Forbidden,(await Send(developer,HttpMethod.Put,aliasRoute,new BackupAliasInput("ut"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(local,HttpMethod.Put,aliasRoute,new BackupAliasInput("../ut"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Send(local,HttpMethod.Put,aliasRoute,new BackupAliasInput("UT"))).StatusCode);
        var catalog=(await local.GetFromJsonAsync<JsonObject>("/api/admin/reduction/catalog"))!;
        Assert.Equal("ut",catalog["backupAliases"]!["do_ke"]!.GetValue<string>());

        async Task<JsonObject> Row()
        {
            var result=(await developer.GetFromJsonAsync<JsonObject>("/api/databases"))!;
            return (JsonObject)Assert.Single(result["databases"]!.AsArray(),row=>row!["name"]!.GetValue<string>()==database)!;
        }
        Assert.Equal(latest,(await Row())["backup"]!["name"]!.GetValue<string>());

        var response=await Send(local,HttpMethod.Post,"/api/admin/reduction/analyses",new AnalysisInput(older));
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var analysisId=(await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
        var store=app.Services.GetRequiredService<Store>();
        Assert.Equal("ut",store.GetJob(analysisId)!.SourceKey);
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,"analyses",analysisId+".json"),
            JsonSerializer.Serialize(Analysis(analysisId) with{SourceKey="ut",BackupName=older}));
        store.UpdateJob(analysisId,"Succeeded","готово");
        var profileInput=new ProfileInput(analysisId,[new("РегистрСведений.Периодный","2026-01-01")]);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(local,HttpMethod.Put,"/api/admin/reduction/profiles/bp_gm",profileInput)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Send(local,HttpMethod.Put,"/api/admin/reduction/profiles/do_ke",profileInput)).StatusCode);
        var profile=Assert.IsType<ReductionProfile>(store.GetProfile("do_ke"));
        Assert.Null(store.GetProfile("ut"));
        var input=new RefreshInput(database,false,false,null,null,true,false,latest);

        Assert.Equal(HttpStatusCode.OK,(await Send(local,HttpMethod.Put,aliasRoute,new BackupAliasInput("missing"))).StatusCode);
        var missing=await Row();
        Assert.Null(missing["backup"]);
        Assert.Equal("missing",missing["backupSourceKey"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh",input with{Shrink=true,BackupName=null})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh",input)).StatusCode);

        Assert.Equal(HttpStatusCode.OK,(await Send(local,HttpMethod.Put,aliasRoute,new BackupAliasInput("ut"))).StatusCode);
        response=await Send(developer,HttpMethod.Post,"/api/refresh",input);
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var jobId=(await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
        var job=store.GetJob(jobId)!;
        Assert.Equal("do_ke",job.SourceKey);
        Assert.Empty(job.Request.BackupName);
        Assert.True(job.Request.ResolveLatestBackup);
        Assert.Empty(job.Request.Rules);

        Assert.Equal(HttpStatusCode.OK,(await Send(local,HttpMethod.Delete,aliasRoute,new{})).StatusCode);
        Assert.Equal("do_ke.bak",(await Row())["backup"]!["name"]!.GetValue<string>());
        Assert.Empty(store.GetJob(jobId)!.Request.BackupName);
        Assert.Contains(store.GetAudit(),entry=>entry.Action=="backup.alias.removed");
    }
    [Fact]
    public async Task CheckDbSettingIsSavedAndCapturedWhenRefreshIsQueued()
    {
        using var app = new Factory();
        var (client, _) = await Prepare(app);
        using var admin = client;
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath, "bp_gm.bak"), "Synthetic backup");
        var store = app.Services.GetRequiredService<Store>();
        var settings = store.GetSettings()!;
        var legacySettings = JsonSerializer.SerializeToNode(settings)!.AsObject();
        legacySettings.Remove(nameof(SqlSettings.RunDbccCheckDb));
        Assert.True(legacySettings.Deserialize<SqlSettings>()!.RunDbccCheckDb);

        var input = new SettingsInput(settings.Server, settings.Authentication, settings.Username, null,
            settings.BackupDirectory, settings.SqlBackupDirectory, settings.TrustServerCertificate, RunDbccCheckDb: false);
        Assert.Equal(HttpStatusCode.OK, (await Send(admin, HttpMethod.Put, "/api/admin/settings", input)).StatusCode);
        var publicSettings = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/settings"))!;
        Assert.False(publicSettings["connection"]!["runDbccCheckDb"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await Send(admin, HttpMethod.Put, "/api/admin/settings", input with { RunDbccCheckDb = null })).StatusCode);
        Assert.False(store.GetSettings()!.RunDbccCheckDb);

        var refresh = new RefreshInput("bp_gm_dev_demo_owner", false, false, null, [], true, false);
        var response = await Send(admin, HttpMethod.Post, "/api/refresh", refresh);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.OK, (await Send(admin, HttpMethod.Put, "/api/admin/settings", input with { RunDbccCheckDb = true })).StatusCode);
        Assert.True(store.GetSettings()!.RunDbccCheckDb);
        Assert.False(store.GetJob(jobId)!.Request.Settings.RunDbccCheckDb);
    }
    [Fact]
    public async Task FullRefreshUsesLatestBackupAndLeavesAdministrativeProfileUntouched()
    {
        using var app=new Factory();var(admin,profile)=await Prepare(app);using var adminClient=admin;
        const string older="bp_gm_30-11-2026.bak";
        const string latest="BP_GM_01-12-2026.BAK";
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,latest),"Synthetic newest backup");
        File.SetLastWriteTimeUtc(Path.Combine(app.DirectoryPath,latest),DateTime.UtcNow.AddDays(-3));
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,older),"Synthetic recently copied backup");
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,"bp_gm.bak"),"Synthetic undated backup");
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,"bp_31-12-2026.bak"),"Synthetic different type");
        using var developer=app.Client();developer.DefaultRequestHeaders.Add("Test-Domain","CORP\\demo_owner");
        var databases=(await developer.GetFromJsonAsync<JsonObject>("/api/databases"))!["databases"]!.AsArray();
        var target=Assert.Single(databases,x=>x!["name"]!.GetValue<string>()=="bp_gm_dev_demo_owner");
        Assert.Equal(latest,target!["backup"]!["name"]!.GetValue<string>());
        var input=new RefreshInput("bp_gm_dev_demo_owner",false,false,null,null,true,false,latest);
        var response=await Send(developer,HttpMethod.Post,"/api/refresh",input);Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var jobJson=(await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var store=app.Services.GetRequiredService<Store>();var job=store.GetJob(jobJson["id"]!.GetValue<string>())!;
        Assert.Empty(job.Request.BackupName);
        Assert.True(job.Request.ResolveLatestBackup);
        Assert.Empty(job.Request.Rules);
        var unchanged=store.GetProfile("bp_gm")!;Assert.Equal(profile.Revision,unchanged.Revision);Assert.Equal(2,unchanged.Rules.Length);Assert.Equal("2026-01-01",unchanged.Rules[0].Before);
        var publicJson=await developer.GetStringAsync("/api/jobs/"+job.Id);
        Assert.DoesNotContain("ProtectedPassword",publicJson,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("Synthetic-sql-password!",publicJson);
        Assert.DoesNotContain(job.Request.Settings.ProtectedPassword,publicJson);
    }
    [Fact]
    public async Task CopyRequiresSameTypeAndAuthorizedDestinationAndDoesNotRequireArchive()
    {
        using var app=new Factory();var(admin,_)=await Prepare(app);using var adminClient=admin;
        using var developer=app.Client();developer.DefaultRequestHeaders.Add("Test-Domain","CORP\\demo_owner");
        const string target="bp_gm_dev_demo_owner";
        const string source="bp_gm_dev_demo_owner2";
        var input=new RefreshInput(target,false,false,null,[],true,false,SourceDatabase:source);
        foreach(var invalid in new[]
        {
            input with {SourceDatabase=target.ToUpperInvariant()},
            input with {SourceDatabase="do_ke_dev_demo_owner"},
            input with {SourceDatabase="bp_gm_dev_missing"},
            input with {Database=source,SourceDatabase=target},
            input with {BackupName="bp_gm.bak"}
        })
            Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh",invalid)).StatusCode);

        var response=await Send(developer,HttpMethod.Post,"/api/refresh",input);
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var body=(await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var job=app.Services.GetRequiredService<Store>().GetJob(body["id"]!.GetValue<string>())!;
        Assert.Equal(source,job.Request.SourceDatabase);
        Assert.Equal(target,job.Target);
        Assert.Empty(job.Request.BackupName);
    }

    [Fact]
    public async Task RequestsCannotAddOtherTableTargetOrPiiAndCannotEditAdminProfile()
    {
        using var app=new Factory();var(admin,profile)=await Prepare(app);using var adminClient=admin;
        using var developer=app.Client();developer.DefaultRequestHeaders.Add("Test-Domain","CORP\\demo_owner");
        var valid=new RefreshInput("bp_gm_dev_demo_owner",true,false,profile.Revision,[new("РегистрСведений.Периодный","2026-01-01")],true,false);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh",valid with{Rules=[new("РегистрСведений.Другой",null)]})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh",valid with{Database="bp_gm_dev_other_owner"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh",valid with{Database="invalid_demo_database"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh",valid with{Pii=true})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh",valid with{ProfileRevision="outdated"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh",valid with{ConfirmReplace=false})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await Send(developer,HttpMethod.Put,"/api/admin/reduction/profiles/bp_gm",new ProfileInput(profile.AnalysisId,[]))).StatusCode);
        Assert.DoesNotContain(app.Services.GetRequiredService<Store>().GetJobs(),j=>j.Kind=="refresh");
    }
    [Fact]
    public async Task HomePageUsesRegularLoginAndAnonymousCannotReadJobs()
    {
        using var app=new Factory{WindowsEnabled=false};using var client=app.Client();var session=(await client.GetFromJsonAsync<JsonObject>("/api/session"))!;
        Assert.False(session["windowsAutoSignIn"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/jobs")).StatusCode);
        var windows=await client.GetAsync("/auth/windows");Assert.Equal(HttpStatusCode.Redirect,windows.StatusCode);Assert.Equal("/login?domain=disabled",windows.Headers.Location!.ToString());
    }
    [Fact]
    public async Task CorporateSwitchAppliesImmediatelyRevokesDomainAndPersistsInSqlite()
    {
        using var app=new Factory();var(admin,profile)=await Prepare(app);using var local=admin;
        using var domain=app.Client();domain.DefaultRequestHeaders.Add("Test-Domain","CORP\\demo_owner");
        Assert.Equal(HttpStatusCode.OK,(await domain.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Send(local,HttpMethod.Put,"/api/admin/security",new SecurityInput([],null,false,true))).StatusCode);
        var session=(await local.GetFromJsonAsync<JsonObject>("/api/session"))!;
        Assert.False(session["windowsEnabled"]!.GetValue<bool>());
        Assert.False(session["windowsAutoSignIn"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Unauthorized,(await domain.GetAsync("/api/jobs")).StatusCode);
        var redirect=await domain.GetAsync("/auth/windows");
        Assert.Equal("/login?domain=disabled",redirect.Headers.Location!.ToString());
        Assert.False(new Store(Path.Combine(app.DirectoryPath,"devrefresh.sqlite")).GetWindowsAccess(
            app.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()).Enabled);
        Assert.Equal(HttpStatusCode.OK,(await Send(local,HttpMethod.Put,"/api/admin/security",new SecurityInput([],null,true,false))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await domain.GetAsync("/api/jobs")).StatusCode);
        Assert.True((await local.GetFromJsonAsync<JsonObject>("/api/session"))!["windowsEnabled"]!.GetValue<bool>());
    }
    [Fact]
    public async Task PreviewWorksFromSavedAnalysisAfterItsDatabaseWasDeleted()
    {
        using var app=new Factory();var(admin,profile)=await Prepare(app);using var local=admin;
        var store=app.Services.GetRequiredService<Store>();
        Assert.Null(store.GetJob(profile.AnalysisId)!.ResourceName);
        var response=await Send(local,HttpMethod.Post,"/api/admin/reduction/preview",new ProfileInput(profile.AnalysisId,profile.Rules));
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var json=(await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var job=store.GetJob(json["id"]!.GetValue<string>())!;
        Assert.Equal("preview",job.Kind);Assert.Null(job.Target);Assert.Equal(profile.AnalysisId,job.Request.Profile!.AnalysisId);
        Assert.Equal(profile.Revision,store.GetProfile(profile.SourceKey)!.Revision);
    }
    [Fact]
    public async Task CleanupIncludesFailedAndInterruptedResourcesButExcludesRunning()
    {
        using var app=new Factory();var(admin,profile)=await Prepare(app);using var local=admin;
        var store=app.Services.GetRequiredService<Store>();var settings=store.GetSettings()!;
        foreach(var status in new[]{"Running","Failed","Interrupted","Succeeded"})
        {
            var job=store.CreateJob("analysis","admin","bp",null,new(settings,"bp.bak",null,[],false,false));
            store.UpdateJob(job.Id,status,"test",resource:"DevRefreshStage_"+job.Id);
        }
        var cleanup=store.GetCleanupJobs();Assert.Equal(3,cleanup.Length);
        Assert.DoesNotContain(cleanup,j=>j.Status=="Running");
        foreach(var job in cleanup)store.ClearResource(job.Id);
        Assert.Empty(store.GetCleanupJobs());
    }
    [Fact]
    public async Task EstimateChangesWithDatesAndCheckboxesWithoutStartingJobsOrSavingProfile()
    {
        using var app=new Factory();var(admin,profile)=await Prepare(app);using var local=admin;
        using var developer=app.Client();developer.DefaultRequestHeaders.Add("Test-Domain","CORP\\demo_owner");
        var input=new ReductionEstimateInput("bp_gm_dev_demo_owner",profile.Revision,profile.Rules);
        var response=await Send(developer,HttpMethod.Post,"/api/refresh/estimate",input);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var initial=(await response.Content.ReadFromJsonAsync<ReductionEstimate>())!;
        Assert.Equal(6,initial.OriginalMB);Assert.Equal(3,initial.RemovedMinMB);Assert.Equal(50,initial.RemovedMinPercent);
        response=await Send(developer,HttpMethod.Post,"/api/refresh/estimate",input with{Rules=[new("РегистрСведений.Периодный","2025-01-01"),new("РегистрСведений.Полный",null)]});
        Assert.Equal(2,(await response.Content.ReadFromJsonAsync<ReductionEstimate>())!.RemovedMinMB);
        response=await Send(developer,HttpMethod.Post,"/api/refresh/estimate",input with{Rules=[]});
        Assert.Equal(0,(await response.Content.ReadFromJsonAsync<ReductionEstimate>())!.RemovedMaxMB);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh/estimate",input with{Database="bp_gm_dev_other_owner"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh/estimate",input with{Rules=[new("РегистрСведений.Другой",null)]})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh/estimate",input with{ProfileRevision="old"})).StatusCode);
        Assert.Single(app.Services.GetRequiredService<Store>().GetJobs());
        Assert.Equal(profile.Revision,app.Services.GetRequiredService<Store>().GetProfile(profile.SourceKey)!.Revision);
    }
    [Fact]
    public async Task AnotherDeveloperCannotReadQueuedRestore()
    {
        using var app=new Factory();var(admin,profile)=await Prepare(app);using var adminClient=admin;
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,"bp_gm.bak"),"Synthetic undated backup");
        using var developer=app.Client();developer.DefaultRequestHeaders.Add("Test-Domain","CORP\\demo_owner");
        var response=await Send(developer,HttpMethod.Post,"/api/refresh",new RefreshInput("bp_gm_dev_demo_owner",false,false,null,null,true,false));
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var job=(await response.Content.ReadFromJsonAsync<JsonObject>())!;
        using var another=app.Client();another.DefaultRequestHeaders.Add("Test-Domain","CORP\\other_owner");
        Assert.Equal(HttpStatusCode.NotFound,(await another.GetAsync("/api/jobs/"+job["id"]!.GetValue<string>())).StatusCode);
    }
    [Fact]
    public async Task ScheduledRefreshIsOwnerVisibleAndCannotStartEarly()
    {
        using var app = new Factory();
        var (admin, profile) = await Prepare(app);
        using var adminClient = admin;
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,"bp_gm.bak"),"Synthetic backup");
        using var developer = app.Client();
        developer.DefaultRequestHeaders.Add("Test-Domain","CORP\\demo_owner");
        var store = app.Services.GetRequiredService<Store>();
        var at = DateTimeOffset.UtcNow.AddHours(2).ToOffset(TimeSpan.FromHours(4));
        var request = new RefreshInput("bp_gm_dev_demo_owner",false,false,null,null,true,false,"bp_gm.bak",at);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh",request with { ScheduledAt=at.AddDays(-1) })).StatusCode);

        var response = await Send(developer,HttpMethod.Post,"/api/refresh",request);
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = job["id"]!.GetValue<string>();
        Assert.Equal("Scheduled",job["status"]!.GetValue<string>());
        Assert.Equal(at,job["scheduledAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(TimeSpan.Zero,store.GetJob(id)!.ScheduledAt!.Value.Offset);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(developer,HttpMethod.Post,"/api/refresh",request)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,(await Send(developer,HttpMethod.Post,"/api/refresh",request with { ScheduledAt=at.AddDays(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,(await Send(developer,HttpMethod.Post,"/api/refresh",request with { ScheduledAt=null })).StatusCode);
        Assert.Equal("Queued",Assert.Single(store.GetQueued()).Status);

        using var other = app.Client();
        other.DefaultRequestHeaders.Add("Test-Domain","CORP\\other_owner");
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync("/api/jobs/"+id)).StatusCode);
        var dashboard = (await developer.GetFromJsonAsync<JsonObject>("/api/jobs/dashboard"))!;
        Assert.Contains(dashboard["jobs"]!.AsArray(),row=>row!["id"]!.GetValue<string>()==id && row["status"]!.GetValue<string>()=="Scheduled");

        var reopened = new Store(Path.Combine(app.DirectoryPath,"devrefresh.sqlite"));
        reopened.InterruptRunningJobs();
        var restored = reopened.GetJob(id)!;
        Assert.Empty(restored.Request.BackupName);
        Assert.True(restored.Request.ResolveLatestBackup);
        Assert.Empty(restored.Request.Rules);
        Assert.Empty(reopened.QueueDueJobs(at.AddTicks(-1)));
        Assert.False(reopened.TryStartJob(id,at.AddTicks(-1)));
        Assert.Equal(id,Assert.Single(reopened.QueueDueJobs(at)));
        Assert.Empty(reopened.QueueDueJobs(at));
        Assert.False(reopened.TryStartJob(id,at.AddTicks(-1)));
        Assert.Equal("Scheduled",reopened.GetJob(id)!.Status);
        Assert.Equal(id,Assert.Single(reopened.QueueDueJobs(at)));
        Assert.True(reopened.TryStartJob(id,at));
        Assert.False(reopened.TryStartJob(id,at));
    }
    [Fact]
    public async Task BackgroundWorkerRunsOverdueRefreshAfterStartupAndWaitsForFutureRefresh()
    {
        using var app = new Factory();
        var store = new Store(Path.Combine(app.DirectoryPath,"devrefresh.sqlite"));
        var settings = new SqlSettings("TEST-SQL","windows","","",app.DirectoryPath,app.DirectoryPath,false);
        var request = new WorkflowRequest(settings,"missing.bak",null,[],false,false);
        var at = DateTimeOffset.UtcNow.AddHours(1);
        var overdue = store.CreateJob("refresh","admin","bp","bp_dev_first",request,at);
        var future = store.CreateJob("refresh","admin","bp","bp_dev_second",request,at);
        void SetDue(string id)
        {
            using var db = DatabaseMigrator.Connect(Path.Combine(app.DirectoryPath,"devrefresh.sqlite"));
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE workflow_jobs SET scheduled_at=$at WHERE id=$id";
            cmd.Parameters.AddWithValue("$id",id);
            cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"));
            cmd.ExecuteNonQuery();
        }
        SetDue(overdue.Id);
        using var running = app.WithWebHostBuilder(builder=>builder.UseSetting("Workflows:Execute","true"));
        using var runningClient = running.CreateClient();
        async Task WaitForAttempt(string id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (store.GetJob(id)!.Status!="Failed") await Task.Delay(50,timeout.Token);
            var job = store.GetJob(id)!;
            Assert.Equal("Резервный файл не найден либо является ссылкой.",job.Error);
            var report = Assert.IsType<WorkflowExecutionReport>(store.GetExecutionReport(id));
            Assert.NotNull(job.StartedAt);
            Assert.True(report.FinishedAt >= job.StartedAt);
            Assert.Equal("Открытие резервного файла",Assert.Single(report.Stages).Name);
        }
        await WaitForAttempt(overdue.Id);
        Assert.Equal("Scheduled",store.GetJob(future.Id)!.Status);
        SetDue(future.Id);
        await WaitForAttempt(future.Id);
    }
    [Fact]
    public async Task BackgroundWorkerRunsDifferentTypesTogetherAndStartsWaitingJobsWhenSlotsFreeUp()
    {
        using var app = new Factory();
        var store = new Store(Path.Combine(app.DirectoryPath,"devrefresh.sqlite"));
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,"test.bak"),"Synthetic backup");
        using var bpListener = new TcpListener(IPAddress.Loopback,0);
        using var utListener = new TcpListener(IPAddress.Loopback,0);
        bpListener.Start();
        utListener.Start();

        WorkflowRequest Request(TcpListener listener,string backup)
        {
            var server = $"tcp:127.0.0.1,{((IPEndPoint)listener.LocalEndpoint).Port}";
            return new(new(server,"windows","","",app.DirectoryPath,app.DirectoryPath,false),backup,null,[],false,false);
        }
        var bp = store.CreateJob("refresh","admin","bp","bp_dev_first",Request(bpListener,"test.bak"));
        var nextBp = store.CreateJob("refresh","admin","bp","bp_dev_second",Request(bpListener,"missing.bak"));
        var ut = store.CreateJob("refresh","admin","ut","ut_dev_first",Request(utListener,"test.bak"));
        var other = store.CreateJob("refresh","admin","other","other_dev_first",Request(utListener,"missing.bak"));

        using var running = app.WithWebHostBuilder(builder=>builder.UseSetting("Workflows:Execute","true"));
        using var client = running.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var bpConnection = await bpListener.AcceptTcpClientAsync(timeout.Token);
        using var utConnection = await utListener.AcceptTcpClientAsync(timeout.Token);
        Assert.Equal("Running",store.GetJob(bp.Id)!.Status);
        Assert.Equal("Running",store.GetJob(ut.Id)!.Status);
        Assert.Equal("Queued",store.GetJob(nextBp.Id)!.Status);
        Assert.Equal("Queued",store.GetJob(other.Id)!.Status);

        async Task WaitForFailure(string id)
        {
            while (store.GetJob(id)!.Status!="Failed") await Task.Delay(25,timeout.Token);
        }
        utConnection.Dispose();
        utListener.Stop();
        await WaitForFailure(other.Id);
        Assert.Equal("Running",store.GetJob(bp.Id)!.Status);
        Assert.Equal("Queued",store.GetJob(nextBp.Id)!.Status);
        bpConnection.Dispose();
        bpListener.Stop();
        await WaitForFailure(nextBp.Id);
    }

    [Fact]
    public async Task BackgroundMaintenanceYieldsTheRestoreSlotAndProtectsItsOwnDatabase()
    {
        using var app = new Factory();
        var path=Path.Combine(app.DirectoryPath,"devrefresh.sqlite");
        var store=new Store(path);
        await File.WriteAllTextAsync(Path.Combine(app.DirectoryPath,"test.bak"),"Synthetic backup");
        using var restoreListener=new TcpListener(IPAddress.Loopback,0);
        using var firstMaintenance=new TcpListener(IPAddress.Loopback,0);
        using var secondMaintenance=new TcpListener(IPAddress.Loopback,0);
        foreach (var listener in new[]{restoreListener,firstMaintenance,secondMaintenance}) listener.Start();

        WorkflowRequest Request(TcpListener listener,string backup)
        {
            var server=$"tcp:127.0.0.1,{((IPEndPoint)listener.LocalEndpoint).Port}";
            return new(new(server,"windows","","",app.DirectoryPath,app.DirectoryPath,false),backup,null,[],false,false);
        }

        WorkflowJob PendingMaintenance(string target,TcpListener listener,DateTimeOffset readyAt)
        {
            var job=store.CreateJob("refresh","admin","bp",target,Request(listener,"test.bak"));
            Assert.True(store.TryStartJob(job.Id,readyAt.AddSeconds(-1)));
            using var db=DatabaseMigrator.Connect(path);
            using var cmd=db.CreateCommand();
            cmd.CommandText="""
                UPDATE workflow_jobs SET ready_at=$at,background_status='Queued',execution_report=$report WHERE id=$id
                """;
            cmd.Parameters.AddWithValue("$at",readyAt.ToString("O"));
            cmd.Parameters.AddWithValue("$id",job.Id);
            cmd.Parameters.AddWithValue("$report",JsonSerializer.Serialize(new WorkflowExecutionReport(readyAt,1000,[],[],null)));
            cmd.ExecuteNonQuery();
            return job;
        }

        var now=DateTimeOffset.UtcNow;
        var first=PendingMaintenance("bp_dev_first",firstMaintenance,now.AddSeconds(-2));
        var second=PendingMaintenance("bp_dev_second",secondMaintenance,now.AddSeconds(-1));
        var foreground=store.CreateJob("refresh","admin","bp","bp_dev_third",Request(restoreListener,"test.bak"));
        using var running=app.WithWebHostBuilder(builder=>builder.UseSetting("Workflows:Execute","true")
            .UseSetting("Workflows:MaxParallelJobs","1"));
        using var client=running.CreateClient();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var restoreConnection=await restoreListener.AcceptTcpClientAsync(timeout.Token);
        Assert.Equal("Running",store.GetJob(foreground.Id)!.Status);
        Assert.Equal("Queued",store.GetJob(first.Id)!.BackgroundStatus);
        Assert.Equal("Queued",store.GetJob(second.Id)!.BackgroundStatus);
        restoreConnection.Dispose();
        restoreListener.Stop();

        using var firstConnection=await firstMaintenance.AcceptTcpClientAsync(timeout.Token);
        Assert.Equal("Running",store.GetJob(first.Id)!.BackgroundStatus);
        var worker=running.Services.GetRequiredService<WorkflowService>();
        var same=store.CreateJob("refresh","admin","bp",first.Target,Request(firstMaintenance,"missing.bak"));
        var other=store.CreateJob("refresh","admin","bp","bp_dev_fourth",Request(firstMaintenance,"missing.bak"));
        worker.Enqueue(same);
        worker.Enqueue(other);
        while (store.GetJob(other.Id)!.Status!="Failed") await Task.Delay(25,timeout.Token);
        Assert.Equal("Running",store.GetJob(first.Id)!.BackgroundStatus);
        Assert.Equal("Queued",store.GetJob(second.Id)!.BackgroundStatus);
        Assert.Equal("Queued",store.GetJob(same.Id)!.Status);

        firstConnection.Dispose();
        firstMaintenance.Stop();
        using var secondConnection=await secondMaintenance.AcceptTcpClientAsync(timeout.Token);
        Assert.Equal("Failed",store.GetJob(same.Id)!.Status);
        var finished=store.GetJob(first.Id)!;
        Assert.Equal("Succeeded",finished.Status);
        Assert.Equal("Failed",finished.BackgroundStatus);
        Assert.Equal(now.AddSeconds(-2),finished.ReadyAt);
        Assert.Contains(store.GetExecutionReport(first.Id)!.Stages,s=>s.Name=="Ожидание фонового освобождения места");
        secondConnection.Dispose();
        secondMaintenance.Stop();
        while (store.GetJob(second.Id)!.Status!="Succeeded") await Task.Delay(25,timeout.Token);
    }
}
