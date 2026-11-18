using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DevRefresh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevRefresh.Tests;

public sealed class RestorePipelineTests
{
    [Theory]
    [InlineData(@"\\CORP_TEST\demo_owner",@"\\CORP\demo_owner")]
    [InlineData(@"\\CORP.EXAMPLE.COM\demo_user",@"\\CORP\demo_user")]
    [InlineData(@"CORP_TEST\User_Name",@"CORP\User_Name")]
    [InlineData(@"\\corp\same",@"\\corp\same")]
    public void RewritePreservesAccountAndLeadingSlashes(string before,string after)
    {
        var result=Assert.Single(RestorePipelineExecutor.Plan([(new byte[16],before)],"CORP",128));
        Assert.Equal(after,result.After);
    }
    [Fact]
    public void PlanningRejectsCollisionsUnknownFormatsAndColumnOverflowBeforeWriting()
    {
        Assert.Throws<InputException>(()=>RestorePipelineExecutor.Plan([
            (new byte[16],@"\\FIRST\same"),(new byte[16],@"\\CORP\SAME")],"CORP",128));
        Assert.Throws<InputException>(()=>RestorePipelineExecutor.Plan([(new byte[16],"user@domain")],"CORP",128));
        Assert.Throws<InputException>(()=>RestorePipelineExecutor.Plan([(new byte[16],@"\\OLD\long_account")],"CORP",10));
        Assert.Throws<InputException>(()=>RestorePipelineRules.Validate("bp.invalid","bak","CORP"));
        Assert.Throws<InputException>(()=>RestorePipelineRules.Validate("bp","bak",@"CORP\user"));
    }

    static async Task<HttpResponseMessage> Send(HttpClient client,HttpMethod method,string path,object? body=null)
    {
        var session=(await client.GetFromJsonAsync<JsonObject>("/api/session"))!;
        using var request=new HttpRequestMessage(method,path){Content=body is null?null:JsonContent.Create(body)};
        request.Headers.Add("X-CSRF-TOKEN",session["csrfToken"]!.GetValue<string>());
        return await client.SendAsync(request);
    }
    [Theory]
    [InlineData("full","bak")]
    [InlineData("shrink","bak")]
    [InlineData("dt","dt")]
    [InlineData("database","bak")]
    public async Task RulesAreAdminOnlySharedByTypeAndArchiveFormatAndCanBeDisabled(string source,string format)
    {
        using var app=new Factory();using var admin=app.Client();
        Assert.Equal(HttpStatusCode.OK,(await Send(admin,HttpMethod.Post,"/api/setup",new SetupInput("admin","Synthetic-pipeline-password!",[]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Send(admin,HttpMethod.Put,"/api/admin/settings",new SettingsInput("TEST-SQL","windows","",null,app.DirectoryPath,app.DirectoryPath,false))).StatusCode);
        var input=new RestorePipelineInput("bp",format,true,"CORP");
        Assert.Equal(HttpStatusCode.OK,(await Send(admin,HttpMethod.Put,"/api/admin/restore-pipelines",input)).StatusCode);
        var store=app.Services.GetRequiredService<Store>();
        Assert.Equal("CORP",Assert.Single(store.GetRestorePipeline("test-sql","BP",format)!.Operations).TargetDomain);
        Assert.Null(store.GetRestorePipeline("OTHER-SQL",input.SourceType,format));
        Assert.Null(store.GetRestorePipeline("TEST-SQL","other_bp",format));
        Assert.Null(store.GetRestorePipeline("TEST-SQL",input.SourceType,format=="bak"?"dt":"bak"));
        foreach(var database in new[]{"bp_dev_one","bp_dev_another_owner","bp_tk"})
            Assert.NotNull(store.GetRestorePipeline("TEST-SQL",Rules.ParseDatabase(database)!.SourceKey,format));
        var request=new WorkflowRequest(store.GetSettings()!,"",null,[],false,true);
        request=source switch{"shrink"=>request with{Shrink=true,UsePreparedShrink=true},"dt"=>request with{UseDt=true},
            "database"=>request with{SourceDatabase="bp_dev_source"},_=>request};
        Assert.Equal(format,RestorePipelineRules.Source(request));
        using var developer=app.Client();developer.DefaultRequestHeaders.Add("Test-Domain","CORP\\owner");
        Assert.Equal(HttpStatusCode.Forbidden,(await Send(developer,HttpMethod.Put,"/api/admin/restore-pipelines",input)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Send(admin,HttpMethod.Put,"/api/admin/restore-pipelines",input with{RewriteDomain=false})).StatusCode);
        Assert.Empty(store.GetRestorePipeline("TEST-SQL",input.SourceType,format)!.Operations);
        Assert.Equal("CORP",store.GetRestorePipeline("TEST-SQL",input.SourceType,format)!.TargetDomain);
        var reopened=new Store(Path.Combine(app.DirectoryPath,"devrefresh.sqlite"));
        Assert.Empty(reopened.GetRestorePipeline("TEST-SQL",input.SourceType,format)!.Operations);
        Assert.Equal(HttpStatusCode.OK,(await Send(admin,HttpMethod.Delete,$"/api/admin/restore-pipelines/{input.SourceType}/{format}?server=TEST-SQL")).StatusCode);
        Assert.Null(store.GetRestorePipeline("TEST-SQL",input.SourceType,format));
    }
}
