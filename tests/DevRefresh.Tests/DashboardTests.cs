using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DevRefresh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevRefresh.Tests;

public sealed class DashboardTests
{
    static WorkflowRequest Request(string path)=>new(new SqlSettings("TEST-SQL","sql","service","synthetic-protected-secret",path,path,false),"bp.bak",null,[],false,false);
    static void Timestamp(Factory app,string id,DateTimeOffset value,bool created=false)
    {
        using var db=DatabaseMigrator.Connect(Path.Combine(app.DirectoryPath,"devrefresh.sqlite"));
        using var cmd=db.CreateCommand();
        cmd.CommandText="UPDATE workflow_jobs SET updated_at=$at"+(created?",created_at=$at":"")+" WHERE id=$id";
        cmd.Parameters.AddWithValue("$at",value.ToString("O"));cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();
    }
    [Fact]
    public void DashboardKeepsAllActiveJobsAndExpiresTerminalStatesAtFiveMinutes()
    {
        using var app=new Factory();using var client=app.Client();
        var store=app.Services.GetRequiredService<Store>();var request=Request(app.DirectoryPath);
        var now=new DateTimeOffset(2030,1,1,12,0,0,TimeSpan.Zero);
        var expected=new List<string>();
        foreach(var status in new[]{"Queued","Running","Succeeded","Failed","Interrupted"})
        {
            var job=store.CreateJob("analysis","admin","bp",null,request);
            store.UpdateJob(job.Id,status,"stage");
            Timestamp(app,job.Id,now.AddMinutes(status is "Queued" or "Running"?-60:-4));
            expected.Add(job.Id);
        }
        foreach(var status in new[]{"Succeeded","Failed","Interrupted"})
        {
            var old=store.CreateJob("analysis","admin","bp",null,request);
            store.UpdateJob(old.Id,status,"old");Timestamp(app,old.Id,now.AddMinutes(-5));
        }
        Assert.Equal(expected.Order(),store.GetDashboardJobs(now).Select(j=>j.Id).Order());
    }
    [Fact]
    public async Task DashboardIsOwnerFilteredAndNotLimitedByTheHistoryPage()
    {
        using var app=new Factory();using var developer=app.Client();
        var store=app.Services.GetRequiredService<Store>();var request=Request(app.DirectoryPath);
        var own=store.CreateJob("refresh","CORP\\demo_owner","bp","bp_dev_demo_owner",request);
        store.UpdateJob(own.Id,"Running","Восстановление",new{SqlCheck="passed",Deleted=new[]{new{Object="Документ.Тест",Rows=42}}});
        Timestamp(app,own.Id,DateTimeOffset.UtcNow.AddDays(-1),created:true);
        var other=store.CreateJob("refresh","CORP\\other_owner","bp","bp_dev_other_owner",request);
        var adminAnalysis=store.CreateJob("analysis","admin","bp",null,request);
        for(var i=0;i<101;i++)
        {
            var old=store.CreateJob("analysis","admin","bp",null,request);
            store.UpdateJob(old.Id,"Succeeded","done");Timestamp(app,old.Id,DateTimeOffset.UtcNow.AddMinutes(-10),created:true);
        }
        Assert.DoesNotContain(store.GetJobs(),j=>j.Id==own.Id);
        Assert.Equal(HttpStatusCode.Unauthorized,(await developer.GetAsync("/api/jobs/dashboard")).StatusCode);
        developer.DefaultRequestHeaders.Add("Test-Domain","CORP\\demo_owner");
        var response=await developer.GetAsync("/api/jobs/dashboard");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var raw=await response.Content.ReadAsStringAsync();var body=JsonNode.Parse(raw)!;
        Assert.Equal(300,body["completedVisibleSeconds"]!.GetValue<int>());
        Assert.Single(body["jobs"]!.AsArray());
        Assert.Equal(own.Id,body["jobs"]![0]!["id"]!.GetValue<string>());
        Assert.DoesNotContain(other.Id,raw);Assert.DoesNotContain(adminAnalysis.Id,raw);
        Assert.DoesNotContain("synthetic-protected-secret",raw);Assert.DoesNotContain("settings",raw,StringComparison.OrdinalIgnoreCase);
        var detail=(await developer.GetFromJsonAsync<JsonObject>("/api/jobs/"+own.Id))!;
        Assert.Equal("passed",detail["result"]!["sqlCheck"]!.GetValue<string>());
        Assert.Equal(42,detail["result"]!["deleted"]![0]!["rows"]!.GetValue<int>());
        Assert.Equal("Документ.Тест",detail["result"]!["deleted"]![0]!["object"]!.GetValue<string>());
        using var admin=app.Client();
        var session=(await admin.GetFromJsonAsync<JsonObject>("/api/session"))!;
        using var setup=new HttpRequestMessage(HttpMethod.Post,"/api/setup"){Content=JsonContent.Create(new SetupInput("admin","Synthetic-dashboard-password!",[]))};
        setup.Headers.Add("X-CSRF-TOKEN",session["csrfToken"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK,(await admin.SendAsync(setup)).StatusCode);
        var all=(await admin.GetFromJsonAsync<JsonObject>("/api/jobs/dashboard"))!["jobs"]!.AsArray();
        Assert.Equal(3,all.Count);
    }

    [Fact]
    public async Task DashboardDurationUsesSuccessfulHistoryOfTheSameDatabaseAndReductionMode()
    {
        using var app = new Factory();
        using var developer = app.Client();
        const string actor = "CORP\\demo_owner";
        const string target = "bp_dev_demo_owner";
        developer.DefaultRequestHeaders.Add("Test-Domain",actor);
        var store = app.Services.GetRequiredService<Store>();
        var request = Request(app.DirectoryPath);
        var start = DateTimeOffset.UtcNow.AddHours(-1);

        void Finish(string database, bool shrink, long milliseconds, string status = "Succeeded", bool legacy = false,
            DateTimeOffset? finishedAt = null)
        {
            var end = finishedAt ?? start.AddMinutes(-10);
            var historical = store.CreateJob("refresh",actor,"bp",database,request with { Shrink = shrink });
            Assert.True(store.TryStartJob(historical.Id,end.AddMilliseconds(-milliseconds)));
            store.UpdateJob(historical.Id,status,"done",executionReport:legacy ? null : new(end,milliseconds,[],[],null));
            Timestamp(app,historical.Id,end);
        }

        Finish(target,false,90000,legacy:true);
        Finish(target.ToUpperInvariant(),false,120000);
        Finish(target,false,600000);
        Finish(target,false,900000,status:"Failed");
        Finish(target,false,900000,finishedAt:start.AddMinutes(1));
        Finish(target,true,300000);
        Finish("other_bp_dev_demo_owner",false,900000);

        async Task CheckEstimate(bool shrink, long expected)
        {
            var job = store.CreateJob("refresh",actor,"bp",target,request with { Shrink = shrink });
            Assert.True(store.TryStartJob(job.Id,start));
            var body = (await developer.GetFromJsonAsync<JsonObject>("/api/jobs/dashboard"))!;
            var row = body["jobs"]!.AsArray().Single(j => j!["id"]!.GetValue<string>() == job.Id)!;
            Assert.Equal(start,row["startedAt"]!.GetValue<DateTimeOffset>());
            Assert.Equal(shrink,row["shrink"]!.GetValue<bool>());
            Assert.Equal(expected,row["estimatedDurationMs"]!.GetValue<long>());
            store.UpdateJob(job.Id,"Interrupted","test");
        }

        await CheckEstimate(false,120000);
        await CheckEstimate(true,300000);
    }

    [Fact]
    public async Task JobDetailsReturnExecutionTimesAndFileSizesAfterFailure()
    {
        using var app = new Factory();
        using var developer = app.Client();
        developer.DefaultRequestHeaders.Add("Test-Domain","CORP\\demo_owner");
        var store = app.Services.GetRequiredService<Store>();
        var job = store.CreateJob("refresh","CORP\\demo_owner","bp","bp_dev_demo_owner",Request(app.DirectoryPath));
        var start = DateTimeOffset.UtcNow;
        Assert.True(store.TryStartJob(job.Id,start));
        var end = start.AddSeconds(3);
        store.UpdateJob(job.Id,"Failed","Удаление связанных строк",error:"Synthetic SQL error",
            executionReport:new(end,3000,
                [new("Удаление связанных строк регистров",3000,[new("Удаление связанных строк","РегистрБухгалтерии.Хозрасчетный.ЗначенияСубконто","dbo._AccRgED",2900)])],
                [new("После восстановления",1000,400,800,100,"NOTHING","SIMPLE")],null));

        var detail = (await developer.GetFromJsonAsync<JsonObject>("/api/jobs/"+job.Id))!;
        Assert.Equal("Failed",detail["status"]!.GetValue<string>());
        Assert.Equal(start,detail["startedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(end,detail["finishedAt"]!.GetValue<DateTimeOffset>());
        var report = detail["executionReport"]!;
        Assert.Equal(3000,report["durationMs"]!.GetValue<long>());
        Assert.Equal(2900,report["stages"]![0]!["tables"]![0]!["durationMs"]!.GetValue<long>());
        Assert.Equal("NOTHING",report["fileSizes"]![0]!["logReuseWait"]!.GetValue<string>());
        Assert.Equal("SIMPLE",report["fileSizes"]![0]!["recoveryModel"]!.GetValue<string>());
    }
}
