using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DevRefresh;

public sealed class McpOAuthDb(DbContextOptions<McpOAuthDb> options) : DbContext(options);
internal sealed record McpTokenAudit(string Actor,string Action,string ClientId);

public static class McpOAuth
{
    public const string Policy = "McpAccess";
    public const string DomainLogin = "domain_login";
    public static void AddMcpFeature(this IServiceCollection services, IConfiguration configuration, string dataPath)
    {
        var settings = McpConfiguration.Load(configuration);
        services.AddSingleton(settings);
        services.AddAuthorization();
        services.AddHttpContextAccessor();
        services.AddScoped<RefreshOperations>();
        services.AddSingleton<IRefreshInspector, RefreshInspector>();
        services.AddScoped<McpPlans>();
        if (!settings.Enabled) return;
        services.AddDbContext<McpOAuthDb>(options => options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(dataPath, "mcp-oauth.sqlite"), Pooling = false }.ToString()).UseOpenIddict());
        services.AddSingleton<McpKeys>();
        services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<McpOAuthDb>())
            .AddServer(options =>
            {
                options.SetIssuer(settings.Issuer);
                options.SetConfigurationEndpointUris("/.well-known/oauth-authorization-server", "/.well-known/openid-configuration");
                options.SetAuthorizationEndpointUris("/oauth/authorize");
                options.SetTokenEndpointUris("/oauth/token");
                options.SetRevocationEndpointUris("/oauth/revoke");
                options.SetJsonWebKeySetEndpointUris("/oauth/keys");
                options.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
                options.RegisterScopes(McpConfiguration.ReadScope, McpConfiguration.RestoreScope);
                options.RegisterResources(settings.Resource);
                options.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(2));
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(14));
                options.UseReferenceAccessTokens().UseReferenceRefreshTokens();
                options.Configure(server => server.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));
                options.AddEventHandler<OpenIddictServerEvents.HandleConfigurationRequestContext>(handler => handler.UseInlineHandler(context =>
                {
                    context.Metadata["registration_endpoint"] = new Uri(settings.Issuer, "oauth/register").AbsoluteUri;
                    return default;
                }));
                options.AddEventHandler<OpenIddictServerEvents.ApplyTokenResponseContext>(handler=>handler.UseInlineHandler(context=>
                {
                    // Complete audit writes before the HTTP response is sent; no work outlives the request.
                    var http=context.Transaction.GetHttpRequest()?.HttpContext;
                    if(context.Error is null&&context.Response.AccessToken is {Length:>0}&&http?.Items[typeof(McpTokenAudit)] is McpTokenAudit audit)
                        http.RequestServices.GetRequiredService<Store>().Audit(audit.Actor,audit.Action,$"OAuth-клиент {audit.ClientId}: обмен завершён.");
                    return default;
                }));
                options.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.AddAudiences(settings.Resource);
                options.EnableAuthorizationEntryValidation();
                options.EnableTokenEntryValidation();
                options.UseAspNetCore();
            });
        services.AddOptions<OpenIddictServerOptions>().Configure<McpKeys>((options, keys) =>
        {
            options.SigningCredentials.Add(new(keys.Signing, SecurityAlgorithms.RsaSha256));
            options.EncryptionCredentials.Add(new(keys.Encryption, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        });
        services.AddAuthorization(options => options.AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).RequireAuthenticatedUser()
            .RequireAssertion(context => context.User.HasScope(McpConfiguration.ReadScope) && context.Resource is HttpContext http
                && DomainUser(context.User, http.RequestServices.GetRequiredService<Store>(), configuration) is not null)));
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, McpAuthorizationResult>();
        services.AddMcpServer(options => options.ServerInfo = new() { Name = "devrefresh", Version = typeof(McpOAuth).Assembly.GetName().Version!.ToString() })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<DevRefreshMcpTools>(System.Text.Json.JsonSerializerOptions.Web);
    }

    public static async Task InitializeMcp(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<McpConfiguration>().Enabled) return;
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<McpOAuthDb>().Database.EnsureCreatedAsync();
        // Force key loading before accepting requests; encrypted keys survive service restarts.
        _ = app.Services.GetRequiredService<McpKeys>();
    }

    public static AppUser? DomainUser(ClaimsPrincipal principal, Store store, IConfiguration configuration)
    {
        if (!store.GetWindowsAccess(configuration).Enabled) return null;
        var name = principal.GetClaim(DomainLogin);
        if (name is null || Rules.OwnerFromIdentity(name) is not { } owner || principal.GetClaim(Claims.Subject) is not { Length: > 0 }) return null;
        return new(name, "domain", store.GetWindowsAdmins().Contains(name, StringComparer.OrdinalIgnoreCase), owner,
            (store.GetWindowsAccess(configuration).TestContourOperators ?? []).Contains(name, StringComparer.OrdinalIgnoreCase));
    }
}

public sealed class McpAuthorizationResult(McpConfiguration configuration) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (context.Request.Path.StartsWithSegments("/mcp") && !result.Succeeded)
        {
            context.Response.StatusCode = result.Challenged ? 401 : 403;
            context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{configuration.Metadata}\", scope=\"{McpConfiguration.ReadScope} {McpConfiguration.RestoreScope}\"";
            await context.Response.WriteAsJsonAsync(new { error = result.Challenged ? "Требуется OAuth-вход." : "Недостаточно прав MCP." });
            return;
        }
        await fallback.HandleAsync(next, context, policy, result);
    }
}
