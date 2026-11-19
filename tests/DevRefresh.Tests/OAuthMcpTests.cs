using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using System.Security.Claims;
using DevRefresh;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevRefresh.Tests;

public sealed class SyntheticNegotiateHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder)
    :AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()=>Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
        new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Name,"CORP\\demo_owner"),new(ClaimTypes.PrimarySid,"S-1-5-21-100-200-300-1001")],Scheme.Name)),Scheme.Name)));
}

public sealed class OAuthMcpTests
{
    private const string Sid="S-1-5-21-100-200-300-1001";
    private const string Account="CORP\\demo_owner";
    private const string Database="bp_gm_dev_demo_owner";
    private const string Redirect="http://127.0.0.1:42123/callback";
    private const string Verifier="synthetic-pkce-verifier-with-at-least-43-characters-12345";
    private sealed class Inspector : IRefreshInspector
    {
        public int Count {get;set;}
        public Task<int> Sessions(SqlSettings settings,string database,CancellationToken token)=>Task.FromResult(Count);
    }
    private sealed class Gateway : ISqlGateway
    {
        public Task<ConnectionResult> Test(SqlSettings settings,CancellationToken token)=>Task.FromResult(new ConnectionResult("TEST-SQL","test","test"));
        public Task<IReadOnlyList<SqlDatabaseInfo>> List(SqlSettings settings,CancellationToken token)=>Task.FromResult<IReadOnlyList<SqlDatabaseInfo>>(
            [new(Database,"ONLINE"),new("bp_gm_dev_other_owner","ONLINE"),new("bp_gm_tk","ONLINE"),new("other_dev_other_owner","ONLINE")]);
    }
    private sealed class Host : IDisposable
    {
        public Inspector Inspector {get;}=new();
        public Factory App {get;}
        public Store Store=>App.Services.GetRequiredService<Store>();
        public HttpClient Browser {get;}
        public HttpClient Anonymous {get;}
        public string FullDirectory=>Path.Combine(App.DirectoryPath,"full");
        public string DtDirectory=>Path.Combine(App.DirectoryPath,"dt");
        public Host(bool syntheticNegotiate=false)
        {
            App=new(){McpEnabled=true,SyntheticNegotiate=syntheticNegotiate,SqlGateway=new Gateway(),RefreshInspector=Inspector};
            Anonymous=App.Client();Browser=Domain();
            using var admin=App.Client();
            var session=admin.GetFromJsonAsync<JsonObject>("/api/session").GetAwaiter().GetResult()!;
            using var setup=new HttpRequestMessage(HttpMethod.Post,"/api/setup"){Content=JsonContent.Create(new SetupInput("admin","Synthetic-oauth-admin-password!",[]))};
            setup.Headers.Add("X-CSRF-TOKEN",session["csrfToken"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.OK,admin.SendAsync(setup).GetAwaiter().GetResult().StatusCode);
            var ibcmd=Path.Combine(App.DirectoryPath,"ibcmd");var copy=Path.Combine(App.DirectoryPath,"copy");
            foreach(var path in new[]{FullDirectory,DtDirectory,ibcmd,copy})Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(ibcmd,"ibcmd.exe"),"Synthetic placeholder; never executed");
            Store.SaveSettings(new("TEST-SQL","sql","service","synthetic-protected-value",FullDirectory,FullDirectory,true,
                CopyBackupDirectory:copy,SqlCopyBackupDirectory:copy,DtRestore:new(true,DtDirectory,ibcmd,"")),"admin");
        }
        public HttpClient Domain(string name=Account,string sid=Sid)
        {
            var client=App.Client();client.DefaultRequestHeaders.Add("Test-Domain",name);client.DefaultRequestHeaders.Add("Test-Sid",sid);return client;
        }
        public async Task<string> Register(string callback=Redirect)
        {
            using var response=await Anonymous.PostAsJsonAsync("/oauth/register",new{client_name="Synthetic MCP test client",redirect_uris=new[]{callback},token_endpoint_auth_method="none"});
            var body=await response.Content.ReadAsStringAsync();Assert.True(response.StatusCode==HttpStatusCode.Created,body);
            return JsonNode.Parse(body)!["client_id"]!.GetValue<string>();
        }
        public static Dictionary<string,string?> Parameters(string client,string scopes,bool validResource=true)=>new()
        {
            ["response_type"]="code",["client_id"]=client,["redirect_uri"]=Redirect,["scope"]=scopes,["state"]="synthetic-state",
            ["code_challenge_method"]="S256",["code_challenge"]=WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier))),
            ["resource"]=validResource?"https://localhost/mcp":"https://other.example.com/mcp"
        };
        public async Task<(string Code,string Client)> Code(string scopes, bool allow=true,HttpClient? browser=null)
        {
            browser??=Browser;var client=await Register();
            var parameters=Parameters(client,scopes);
            var response=await browser.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize",parameters));
            var html=await response.Content.ReadAsStringAsync();Assert.True(response.StatusCode==HttpStatusCode.OK,html);
            Assert.Contains(Account.Split('\\')[1],html);
            var fields=Regex.Matches(html,"<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
                .ToDictionary(m=>WebUtility.HtmlDecode(m.Groups[1].Value),m=>WebUtility.HtmlDecode(m.Groups[2].Value));
            fields["decision"]=allow?"allow":"deny";
            var consent=await browser.PostAsync("/oauth/authorize",new FormUrlEncodedContent(fields));
            var result=await consent.Content.ReadAsStringAsync();Assert.True(consent.StatusCode==HttpStatusCode.Redirect,result);
            var query=QueryHelpers.ParseQuery(consent.Headers.Location!.Query);
            Assert.Equal("synthetic-state",query["state"].ToString());
            return(query[allow?"code":"error"].ToString(),client);
        }
        public Task<HttpResponseMessage> Exchange(string code,string client,string verifier=Verifier)=>Anonymous.PostAsync("/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string,string>{{"grant_type","authorization_code"},{"code",code},{"client_id",client},
                {"redirect_uri",Redirect},{"code_verifier",verifier},{"resource","https://localhost/mcp"}}));
        public async Task<(string Access,string Refresh,string Client)> Tokens(bool restore=true)
        {
            var (code,client)=await Code($"{McpConfiguration.ReadScope} {(restore?McpConfiguration.RestoreScope:"")} offline_access");
            var response=await Exchange(code,client);var body=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,body);
            var json=JsonNode.Parse(body)!;return(json["access_token"]!.GetValue<string>(),json["refresh_token"]!.GetValue<string>(),client);
        }
        public Task<HttpResponseMessage> Refresh(string token,string client)=>Anonymous.PostAsync("/oauth/token",new FormUrlEncodedContent(new Dictionary<string,string>
            {{"grant_type","refresh_token"},{"refresh_token",token},{"client_id",client},{"resource","https://localhost/mcp"}}));
        public async Task<JsonNode> Rpc(string access,string method,object? parameters=null)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/mcp"){Content=JsonContent.Create(new{jsonrpc="2.0",id=1,method,@params=parameters})};
            request.Headers.Authorization=new("Bearer",access);request.Headers.Accept.Add(new("application/json"));request.Headers.Accept.Add(new("text/event-stream"));
            request.Headers.Add("MCP-Protocol-Version","2025-11-25");
            using var response=await Anonymous.SendAsync(request);var text=await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode,text);
            if(response.Content.Headers.ContentType?.MediaType=="text/event-stream")text=text.Split('\n').First(x=>x.StartsWith("data: ",StringComparison.Ordinal))[6..];
            return JsonNode.Parse(text)!;
        }
        public async Task<JsonNode> Tool(string access,string name,object? arguments=null)
        {
            var rpc=await Rpc(access,"tools/call",new{name,arguments=arguments??new{}});
            Assert.Null(rpc["error"]);return rpc["result"]!;
        }
        public static JsonNode Payload(JsonNode result)
        {
            var value=result["structuredContent"]??JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
            return value["result"]??value;
        }
        public void Dispose(){Browser.Dispose();Anonymous.Dispose();App.Dispose();}
    }

    [Fact]
    public async Task DisabledMcpHasNoOAuthEndpointsAndConnectionsRequireWebLogin()
    {
        using var app=new Factory();using var client=app.Client();
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/.well-known/oauth-protected-resource")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.PostAsync("/mcp",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/mcp/connections")).StatusCode);
    }

    [Fact]
    public async Task DiscoveryAdvertisesPkceAndRegistrationAndChallengesContainResourceMetadata()
    {
        using var host=new Host();
        var metadata=(await host.Anonymous.GetFromJsonAsync<JsonObject>("/.well-known/oauth-protected-resource/mcp"))!;
        Assert.Equal("https://localhost/mcp",metadata["resource"]!.GetValue<string>());
        var discovery=(await host.Anonymous.GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server"))!;
        Assert.Equal("https://localhost/oauth/register",discovery["registration_endpoint"]!.GetValue<string>());
        Assert.Equal(new[]{"S256"},discovery["code_challenge_methods_supported"]!.AsArray().Select(x=>x!.GetValue<string>()));
        using var response=await host.Anonymous.PostAsync("/mcp",null);
        Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        Assert.Contains("resource_metadata=\"https://localhost/.well-known/oauth-protected-resource/mcp\"",response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData("https://unapproved.example.com/callback")]
    [InlineData("http://127.0.0.1.attacker.example.com/callback")]
    [InlineData("http://169.254.169.254/callback")]
    [InlineData("http://user@127.0.0.1/callback")]
    public async Task RegistrationRejectsUnapprovedRedirects(string redirect)
    {
        using var host=new Host();
        Assert.Equal(HttpStatusCode.BadRequest,(await host.Anonymous.PostAsJsonAsync("/oauth/register",new{redirect_uris=new[]{redirect}})).StatusCode);
    }

    [Fact]
    public async Task ConsentDenyAndWrongAudienceCannotIssueCodes()
    {
        using var host=new Host();var (error,_)=await host.Code(McpConfiguration.ReadScope,allow:false);
        Assert.Equal("access_denied",error);
        var client=await host.Register();var parameters=Host.Parameters(client,McpConfiguration.ReadScope,false);
        using var response=await host.Browser.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize",parameters));
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        Assert.Contains("invalid_target",await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConsentRequiresCsrfAndAuthorizationCodeIsBoundToPkceAndSingleUse()
    {
        using var host=new Host();var client=await host.Register();var parameters=Host.Parameters(client,McpConfiguration.ReadScope);
        var fields=parameters.ToDictionary(x=>x.Key,x=>x.Value!);fields["decision"]="allow";
        Assert.Equal(HttpStatusCode.BadRequest,(await host.Browser.PostAsync("/oauth/authorize",new FormUrlEncodedContent(fields))).StatusCode);
        var (code,registered)=await host.Code(McpConfiguration.ReadScope);
        Assert.Equal(HttpStatusCode.BadRequest,(await host.Exchange(code,registered,Verifier+"wrong")).StatusCode);
        (code,registered)=await host.Code(McpConfiguration.ReadScope);
        Assert.Equal(HttpStatusCode.OK,(await host.Exchange(code,registered)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await host.Exchange(code,registered)).StatusCode);
    }

    [Fact]
    public async Task TokensIdentifyTheUserAndExposeSevenMethodsButCannotAuthenticateWebAdminApi()
    {
        using var host=new Host();var tokens=await host.Tokens();
        var identity=Host.Payload(await host.Tool(tokens.Access,"get_identity"));
        Assert.True(identity["name"] is not null,identity.ToJsonString());
        Assert.Equal(Account,identity["name"]!.GetValue<string>());
        Assert.False(identity["isAdmin"]!.GetValue<bool>());
        var tools=await host.Rpc(tokens.Access,"tools/list");Assert.Equal(7,tools["result"]!["tools"]!.AsArray().Count);
        var databases=Host.Payload(await host.Tool(tokens.Access,"list_databases"));
        Assert.Equal(new[]{Database},databases["databases"]!.AsArray().Select(x=>x!["name"]!.GetValue<string>()));
        using var request=new HttpRequestMessage(HttpMethod.Get,"/api/admin/settings");request.Headers.Authorization=new("Bearer",tokens.Access);
        Assert.Equal(HttpStatusCode.Unauthorized,(await host.Anonymous.SendAsync(request)).StatusCode);
        Assert.DoesNotContain(tokens.Access,File.ReadAllText(Path.Combine(host.App.DirectoryPath,"mcp-oauth.sqlite")));
    }

    [Fact]
    public async Task ReadOnlyGrantCannotPrepareRestoreAndIdentityHeadersCannotReplaceBearer()
    {
        using var host=new Host();var tokens=await host.Tokens(restore:false);
        var result=await host.Tool(tokens.Access,"prepare_restore",new{database=Database,mode="full"});
        Assert.True(result["isError"]!.GetValue<bool>());
        using var request=new HttpRequestMessage(HttpMethod.Post,"/mcp"){Content=JsonContent.Create(new{})};
        request.Headers.Add("X-Remote-User",Account);
        Assert.Equal(HttpStatusCode.Unauthorized,(await host.Browser.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task RevokingOwnConnectionInvalidatesAccessAndRefreshTokens()
    {
        using var host=new Host();var tokens=await host.Tokens();
        var connections=(await host.Browser.GetFromJsonAsync<JsonObject>("/api/mcp/connections"))!;
        var id=connections["connections"]![0]!["id"]!.GetValue<string>();
        using(var other=host.Domain("CORP\\other_owner","S-1-5-21-100-200-300-1002"))
        {
            var otherSession=(await other.GetFromJsonAsync<JsonObject>("/api/session"))!;
            using var wrong=new HttpRequestMessage(HttpMethod.Delete,"/api/mcp/connections/"+id);
            wrong.Headers.Add("X-CSRF-TOKEN",otherSession["csrfToken"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.NotFound,(await other.SendAsync(wrong)).StatusCode);
        }
        var session=(await host.Browser.GetFromJsonAsync<JsonObject>("/api/session"))!;
        using var revoke=new HttpRequestMessage(HttpMethod.Delete,"/api/mcp/connections/"+id);
        revoke.Headers.Add("X-CSRF-TOKEN",session["csrfToken"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent,(await host.Browser.SendAsync(revoke)).StatusCode);
        using var mcp=new HttpRequestMessage(HttpMethod.Post,"/mcp"){Content=JsonContent.Create(new{})};mcp.Headers.Authorization=new("Bearer",tokens.Access);
        Assert.Equal(HttpStatusCode.Unauthorized,(await host.Anonymous.SendAsync(mcp)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await host.Refresh(tokens.Refresh,tokens.Client)).StatusCode);
    }

    [Fact]
    public async Task RefreshRotatesTokensAndRoleChangesApplyWithoutIssuingNewTokens()
    {
        using var host=new Host();var tokens=await host.Tokens();
        using var refreshed=await host.Refresh(tokens.Refresh,tokens.Client);
        Assert.Equal(HttpStatusCode.OK,refreshed.StatusCode);
        var body=(await refreshed.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotEqual(tokens.Refresh,body["refresh_token"]!.GetValue<string>());
        host.Store.SaveSecurity([],null,"admin",new WindowsAccess(true,false,[Account]));
        var identity=Host.Payload(await host.Tool(body["access_token"]!.GetValue<string>(),"get_identity"));
        Assert.True(identity["canRefreshTestContour"]!.GetValue<bool>());
        host.Store.SaveSecurity([],null,"admin",new WindowsAccess(true,false));
        var result=await host.Tool(body["access_token"]!.GetValue<string>(),"prepare_restore",new{database="bp_gm_tk",mode="full"});
        Assert.True(result["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("full","dt",".dt")]
    [InlineData("dt","full",".bak")]
    public async Task FullAndDtFallbackPinsActualArchiveAndUsesMatchingPipeline(string requested,string effective,string extension)
    {
        using var host=new Host();var tokens=await host.Tokens();
        var root=extension==".dt"?host.DtDirectory:host.FullDirectory;
        await File.WriteAllTextAsync(Path.Combine(root,"bp_gm_19-11-2026"+extension),"Synthetic archive; never restored");
        host.Store.SaveRestorePipeline(new("bp_gm",extension[1..],true,"EXAMPLE"),"admin");
        var prepared=Host.Payload(await host.Tool(tokens.Access,"prepare_restore",new{database=Database,mode=requested}));
        Assert.Equal(effective,prepared["effectiveMode"]!.GetValue<string>());
        Assert.Equal("EXAMPLE",prepared["operations"]![0]!["targetDomain"]!.GetValue<string>());
        var args=new{plan_id=prepared["planId"]!.GetValue<string>(),confirm_replace=true,confirm_disconnect=false,request_id=Guid.NewGuid().ToString()};
        var first=Host.Payload(await host.Tool(tokens.Access,"start_restore",args));
        var repeated=Host.Payload(await host.Tool(tokens.Access,"start_restore",args));
        Assert.Equal(first["id"]!.GetValue<string>(),repeated["id"]!.GetValue<string>());
        var job=Assert.Single(host.Store.GetJobs());Assert.Equal(effective=="dt",job.Request.UseDt);
        Assert.False(job.Request.ResolveLatestBackup);Assert.Equal("bp_gm_19-11-2026"+extension,job.Request.BackupName);
        Assert.Equal(Account,job.Actor);Assert.Equal(Sid,job.Request.Selection!.Subject);
    }

    [Fact]
    public async Task DeveloperCopyRequiresExplicitSourceEvenWhenOnlyOneCandidateExists()
    {
        using var host=new Host();var tokens=await host.Tokens();
        var missing=Host.Payload(await host.Tool(tokens.Access,"prepare_restore",new{database=Database,mode="developer_copy"}));
        Assert.Equal("source_required",missing["status"]!.GetValue<string>());Assert.Single(missing["sources"]!.AsArray());
        Assert.Empty(host.Store.GetJobs());
        var selected=Host.Payload(await host.Tool(tokens.Access,"prepare_restore",new{database=Database,mode="developer_copy",source_database="bp_gm_dev_other_owner"}));
        Assert.Equal("bp_gm_dev_other_owner",selected["source"]!.GetValue<string>());
        var foreign=await host.Tool(tokens.Access,"prepare_restore",new{database="bp_gm_dev_other_owner",mode="developer_copy",source_database=Database});
        Assert.True(foreign["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ChangedArchiveAndMissingDisconnectPermissionCannotStartAPlan()
    {
        using var host=new Host();var tokens=await host.Tokens();var file=Path.Combine(host.FullDirectory,"bp_gm.bak");
        await File.WriteAllTextAsync(file,"Synthetic archive");
        var plan=Host.Payload(await host.Tool(tokens.Access,"prepare_restore",new{database=Database,mode="full"}));
        var args=new{plan_id=plan["planId"]!.GetValue<string>(),confirm_replace=true,confirm_disconnect=false,request_id=Guid.NewGuid().ToString()};
        host.Inspector.Count=1;
        Assert.True((await host.Tool(tokens.Access,"start_restore",args))["isError"]!.GetValue<bool>());
        host.Inspector.Count=0;await File.AppendAllTextAsync(file,"changed");
        Assert.True((await host.Tool(tokens.Access,"start_restore",args))["isError"]!.GetValue<bool>());
        Assert.Empty(host.Store.GetJobs());
    }

    [Fact]
    public async Task OfficialMcpClientCanConnectDiscoverAndCallTools()
    {
        using var host=new Host();var tokens=await host.Tokens();
        using var http=host.App.Client();http.DefaultRequestHeaders.Authorization=new("Bearer",tokens.Access);
        await using var transport=new HttpClientTransport(new(){Endpoint=new Uri("https://localhost/mcp")},http);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client=await McpClient.CreateAsync(transport,cancellationToken:timeout.Token);
        var tools=await client.ListToolsAsync(cancellationToken:timeout.Token);
        Assert.Equal(7,tools.Count);
        var result=await client.CallToolAsync("get_identity",cancellationToken:timeout.Token);
        Assert.NotEqual(true,result.IsError);
        Assert.Contains("demo_owner",string.Join("",result.Content.OfType<TextContentBlock>().Select(x=>x.Text)));
    }

    [Fact]
    public async Task DisablingDomainAccessBlocksExistingTokensAndRefresh()
    {
        using var host=new Host();var tokens=await host.Tokens();
        host.Store.SaveSecurity([],null,"admin",new WindowsAccess(false,false));
        using var request=new HttpRequestMessage(HttpMethod.Post,"/mcp"){Content=JsonContent.Create(new{})};
        request.Headers.Authorization=new("Bearer",tokens.Access);
        Assert.Equal(HttpStatusCode.Forbidden,(await host.Anonymous.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await host.Refresh(tokens.Refresh,tokens.Client)).StatusCode);
    }

    [Fact]
    public async Task PlanCannotBeUsedByAnotherGrantAndSettingsChangesInvalidateIt()
    {
        using var host=new Host();var tokens=await host.Tokens();
        await File.WriteAllTextAsync(Path.Combine(host.FullDirectory,"bp_gm.bak"),"Synthetic archive");
        var plan=Host.Payload(await host.Tool(tokens.Access,"prepare_restore",new{database=Database,mode="full"}));
        var args=new{plan_id=plan["planId"]!.GetValue<string>(),confirm_replace=true,confirm_disconnect=false,request_id=Guid.NewGuid().ToString()};
        var anotherGrant=await host.Tokens();
        Assert.True((await host.Tool(anotherGrant.Access,"start_restore",args))["isError"]!.GetValue<bool>());
        host.Store.SaveRestorePipeline(new("bp_gm","bak",true,"EXAMPLE"),"admin");
        Assert.True((await host.Tool(tokens.Access,"start_restore",args))["isError"]!.GetValue<bool>());
        Assert.Empty(host.Store.GetJobs());
    }

    [Fact]
    public async Task MissingPkceAndChangedRedirectAreRejectedBeforeWindowsLogin()
    {
        using var host=new Host();var client=await host.Register();
        var parameters=Host.Parameters(client,McpConfiguration.ReadScope);parameters.Remove("code_challenge");
        using var missing=await host.Anonymous.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize",parameters));
        Assert.Equal(HttpStatusCode.BadRequest,missing.StatusCode);
        Assert.Contains("invalid_request",await missing.Content.ReadAsStringAsync());
        parameters=Host.Parameters(client,McpConfiguration.ReadScope);parameters["redirect_uri"]="https://unapproved.example.com/callback";
        using var wrong=await host.Anonymous.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize",parameters));
        Assert.Equal(HttpStatusCode.BadRequest,wrong.StatusCode);Assert.Null(wrong.Headers.Location);
    }

    [Fact]
    public async Task ConcurrentStartOfOnePlanCreatesOnlyOneJob()
    {
        using var host=new Host();var tokens=await host.Tokens();
        await File.WriteAllTextAsync(Path.Combine(host.FullDirectory,"bp_gm.bak"),"Synthetic archive");
        var plan=Host.Payload(await host.Tool(tokens.Access,"prepare_restore",new{database=Database,mode="full"}));
        var args=new{plan_id=plan["planId"]!.GetValue<string>(),confirm_replace=true,confirm_disconnect=false,request_id=Guid.NewGuid().ToString()};
        var results=await Task.WhenAll(host.Tool(tokens.Access,"start_restore",args),host.Tool(tokens.Access,"start_restore",args));
        foreach(var result in results)Assert.NotEqual(true,result["isError"]?.GetValue<bool>());
        Assert.Equal(Host.Payload(results[0])["id"]!.GetValue<string>(),Host.Payload(results[1])["id"]!.GetValue<string>());
        Assert.Single(host.Store.GetJobs());
    }

    [Fact]
    public async Task ExpiredPlanAndUnconfirmedReplaceCannotCreateAJob()
    {
        using var host=new Host();var tokens=await host.Tokens();
        await File.WriteAllTextAsync(Path.Combine(host.FullDirectory,"bp_gm.bak"),"Synthetic archive");
        var plan=Host.Payload(await host.Tool(tokens.Access,"prepare_restore",new{database=Database,mode="full"}));
        var id=plan["planId"]!.GetValue<string>();var requestId=Guid.NewGuid().ToString();
        Assert.True((await host.Tool(tokens.Access,"start_restore",new{plan_id=id,confirm_replace=false,confirm_disconnect=false,request_id=requestId}))["isError"]!.GetValue<bool>());
        using(var db=DatabaseMigrator.Connect(Path.Combine(host.App.DirectoryPath,"devrefresh.sqlite")))
        {
            using var command=db.CreateCommand();command.CommandText="UPDATE mcp_restore_plans SET expires_at=$expired WHERE id=$id";
            command.Parameters.AddWithValue("$expired",DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));command.Parameters.AddWithValue("$id",id);command.ExecuteNonQuery();
        }
        Assert.True((await host.Tool(tokens.Access,"start_restore",new{plan_id=id,confirm_replace=true,confirm_disconnect=false,request_id=requestId}))["isError"]!.GetValue<bool>());
        Assert.Empty(host.Store.GetJobs());
    }

    [Fact]
    public async Task ConsentEscapesClientMetadataAndRequiresAnExplicitDecision()
    {
        using var host=new Host();
        using var registered=await host.Anonymous.PostAsJsonAsync("/oauth/register",new{client_name="MCP <script>alert(1)</script>",redirect_uris=new[]{Redirect}});
        Assert.Equal(HttpStatusCode.Created,registered.StatusCode);
        var client=(await registered.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>();
        var response=await host.Browser.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize",Host.Parameters(client,"devrefresh.read devrefresh.restore offline_access")));
        var html=await response.Content.ReadAsStringAsync();Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.DoesNotContain("<script>",html);Assert.Contains("&lt;script&gt;",html);
        Assert.Contains("name=\"decision\" value=\"allow\"",html);Assert.Contains("__RequestVerificationToken",html);
        if(Environment.GetEnvironmentVariable("DEVREFRESH_TEST_CONSENT_CAPTURE") is {Length:>0} capture)
            await File.WriteAllTextAsync(capture,Regex.Replace(html,"<input type=\"hidden\"[^>]*>","").Replace("<button ","<button disabled "));
    }

    [Fact]
    public async Task FirstNegotiateLoginCreatesMatchingConsentCsrfAndDomainCookie()
    {
        using var host=new Host(syntheticNegotiate:true);
        // This browser has neither injected identity headers nor a prior login cookie.
        var (code,client)=await host.Code(McpConfiguration.ReadScope,browser:host.Anonymous);
        Assert.False(string.IsNullOrEmpty(code));
        Assert.Equal(HttpStatusCode.OK,(await host.Exchange(code,client)).StatusCode);
        var session=(await host.Anonymous.GetFromJsonAsync<JsonObject>("/api/session"))!;
        Assert.Equal(Account,session["user"]!["name"]!.GetValue<string>());
    }
}
