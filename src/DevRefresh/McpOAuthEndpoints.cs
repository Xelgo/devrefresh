using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Http.Extensions;
using ModelContextProtocol.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DevRefresh;

public sealed record McpRegistration(
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? AuthenticationMethod = null,
    [property: JsonPropertyName("grant_types")] string[]? GrantTypes = null,
    [property: JsonPropertyName("response_types")] string[]? ResponseTypes = null);

public static class McpOAuthEndpoints
{
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
    private static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");
    public static string? WindowsSid(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.PrimarySid)
        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? (OperatingSystem.IsWindows() && principal.Identity is System.Security.Principal.WindowsIdentity windows ? windows.User?.Value : null);

    public static bool IsLocalReturn(string? value) => value is { Length: > 0 } && value.StartsWith('/')
        && !value.StartsWith("//", StringComparison.Ordinal) && !value.Contains('\\') && !value.Any(char.IsControl);

    public static void UseMcpBoundary(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var oauth = path.StartsWithSegments("/oauth") || path.StartsWithSegments("/mcp")
                || path.StartsWithSegments("/.well-known/oauth-authorization-server")
                || path.StartsWithSegments("/.well-known/openid-configuration")
                || path.StartsWithSegments("/.well-known/oauth-protected-resource");
            if (!oauth) { await next(context); return; }
            var settings = context.RequestServices.GetRequiredService<McpConfiguration>();
            if (!settings.Enabled) { context.Response.StatusCode = 404; return; }
            if (!context.Request.IsHttps || !string.Equals(context.Request.Host.Value, settings.Issuer.Authority, StringComparison.OrdinalIgnoreCase))
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Используйте настроенный HTTPS-адрес MCP." }); return; }
            if (path.StartsWithSegments("/mcp") && context.Request.Headers.TryGetValue("Origin", out var origin)
                && !string.Equals(origin, settings.Issuer.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            { context.Response.StatusCode = 403; return; }
            await next(context);
        });
    }

    public static void MapMcpFeature(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<McpConfiguration>();
        if (!settings.Enabled)
        {
            app.MapGet("/api/mcp/connections", () => Results.Ok(new { Enabled = false, Connections = Array.Empty<object>() }));
            return;
        }
        foreach (var path in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp" })
            app.MapGet(path, () => Results.Ok(new
            {
                resource = settings.Resource,
                authorization_servers = new[] { settings.Issuer.AbsoluteUri },
                scopes_supported = new[] { McpConfiguration.ReadScope, McpConfiguration.RestoreScope },
                bearer_methods_supported = new[] { "header" },
                resource_name = "DevRefresh"
            }));

        app.MapPost("/oauth/register", async (McpRegistration input, IOpenIddictApplicationManager manager, CancellationToken token) =>
        {
            if (input.RedirectUris is not { Length: > 0 and <= 5 } || input.RedirectUris.Any(x => !settings.AllowsRedirect(x))
                || input.AuthenticationMethod is not (null or "none") || input.ClientName is { Length: > 100 }
                || input.GrantTypes?.Any(x => x is not ("authorization_code" or "refresh_token")) == true
                || input.ResponseTypes?.Any(x => x != "code") == true)
                return Results.BadRequest(new { error = "invalid_client_metadata", error_description = "Разрешены public clients с PKCE; HTTPS callback нужно разрешить администратору, либо используйте loopback callback." });
            var id = Guid.NewGuid().ToString("N");
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = id,
                ClientType = ClientTypes.Public,
                ConsentType = ConsentTypes.Explicit,
                DisplayName = string.IsNullOrWhiteSpace(input.ClientName) ? "MCP client" : input.ClientName
            };
            foreach (var uri in input.RedirectUris) descriptor.RedirectUris.Add(new(uri));
            descriptor.Permissions.UnionWith([Permissions.Endpoints.Authorization,Permissions.Endpoints.Token,Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,Permissions.GrantTypes.RefreshToken,Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope+McpConfiguration.ReadScope,Permissions.Prefixes.Scope+McpConfiguration.RestoreScope,
                Permissions.Prefixes.Resource+settings.Resource]);
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
            await manager.CreateAsync(descriptor, token);
            return Results.Json(new
            {
                client_id = id,
                client_name = descriptor.DisplayName,
                redirect_uris = input.RedirectUris,
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" }
            }, statusCode: 201);
        }).RequireRateLimiting("oauth-registration");

        app.MapMethods("/oauth/authorize", ["GET", "POST"], Authorize);
        app.MapPost("/oauth/token", Exchange).RequireRateLimiting("oauth-token");
        app.MapGet("/api/mcp/connections", Connections);
        app.MapDelete("/api/mcp/connections/{id}", Revoke);
        app.MapMcp("/mcp").RequireAuthorization(McpOAuth.Policy);
    }

    private static IResult OAuthError(string error, string description) => Results.Forbid(new AuthenticationProperties(
        new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
        }), [Scheme]);

    private static async Task<IResult> Authorize(HttpContext context, Store store, SessionService sessions,
        IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations, IAntiforgery csrf, McpConfiguration settings)
    {
        var request = context.GetOpenIddictServerRequest()!;
        if (store.GetAdmin() is null) return OAuthError(Errors.AccessDenied, "Администратору нужно завершить первоначальную настройку службы.");
        if (request.RedirectUri is null || !settings.AllowsRedirect(request.RedirectUri))
            return OAuthError(Errors.InvalidRequest, "Адрес возврата больше не разрешён администратором.");
        if (!request.GetResources().SequenceEqual(new[] { settings.Resource })) return OAuthError(Errors.InvalidTarget, "Укажите resource с точным URL MCP этой службы.");
        if (!request.GetScopes().Contains(McpConfiguration.ReadScope) || request.GetScopes().Any(x => x is not (McpConfiguration.ReadScope or McpConfiguration.RestoreScope or Scopes.OfflineAccess)))
            return OAuthError(Errors.InvalidScope, "Требуется devrefresh.read; дополнительно доступны devrefresh.restore и offline_access.");
        if (request.CodeChallengeMethod != CodeChallengeMethods.Sha256) return OAuthError(Errors.InvalidRequest, "Требуется PKCE S256.");
        context.Response.Headers.ContentSecurityPolicy = BrowserSecurityPolicy.ForOAuthConsent(settings, request.RedirectUri);
        if (!store.GetWindowsAccess(context.RequestServices.GetRequiredService<IConfiguration>()).Enabled)
            return OAuthError(Errors.AccessDenied, "Доменный вход отключён администратором.");
        var user = sessions.Current(context);
        var sid = WindowsSid(context.User);
        if (user is not { Kind: "domain" } || string.IsNullOrEmpty(sid))
        {
            var result = await context.AuthenticateAsync(NegotiateDefaults.AuthenticationScheme);
            if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true)
                return Results.Challenge(new AuthenticationProperties(), [NegotiateDefaults.AuthenticationScheme]);
            var name = result.Principal.Identity.Name;
            sid = WindowsSid(result.Principal);
            if (name is null || Rules.OwnerFromIdentity(name) is null || string.IsNullOrEmpty(sid))
                return OAuthError(Errors.AccessDenied, "Не удалось подтвердить доменную учётку и Windows SID.");
            await SessionService.SignIn(context, name, "domain", sid: sid);
            // Continue as the Windows identity confirmed in this request, never a name from OAuth parameters.
            user = new(name, "domain", false, Rules.OwnerFromIdentity(name));
        }
        var application = await applications.FindByClientIdAsync(request.ClientId!) ?? throw new InvalidOperationException("OAuth client is missing.");
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            var hidden = string.Concat(context.Request.Query.SelectMany(p => p.Value.Select(v => $"<input type=\"hidden\" name=\"{E(p.Key)}\" value=\"{E(v)}\">")));
            var anti = csrf.GetAndStoreTokens(context);
            var restore = request.GetScopes().Contains(McpConfiguration.RestoreScope);
            var body = $"""
                <!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Доступ MCP — DevRefresh</title><link rel="stylesheet" href="/styles.css"></head>
                <body><main class="oauth-consent"><div class="eyebrow">DevRefresh · Подключение MCP</div><h1>Разрешить доступ?</h1>
                <p>Доменная учётка: <strong>{E(user.Name)}</strong></p><p>Клиент: <strong>{E(await applications.GetDisplayNameAsync(application))}</strong></p>
                <p>Адрес возврата: <span class="mono">{E(request.RedirectUri)}</span></p>
                <ul><li>Просмотр доступных баз, копий и заданий.</li>{(restore ? "<li>Восстановление доступных вам баз с заменой их содержимого.</li>" : "")}</ul>
                <p>Действуют ваши текущие права devRefresh. Доступ можно отозвать в разделе «Подключения MCP».</p>
                <form method="post" action="/oauth/authorize">{hidden}<input type="hidden" name="{E(anti.FormFieldName)}" value="{E(anti.RequestToken)}">
                <div class="dialog-actions"><button class="button" name="decision" value="deny">Отказать</button><button class="button primary" name="decision" value="allow">Разрешить</button></div></form></main></body></html>
                """;
            return Results.Content(body, "text/html; charset=utf-8");
        }
        await csrf.ValidateRequestAsync(context);
        if (context.Request.Form["decision"] != "allow") return OAuthError(Errors.AccessDenied, "Пользователь отказал в доступе.");
        var identity = new ClaimsIdentity("OAuth", Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, sid).SetClaim(Claims.Name, user.Name).SetClaim(McpOAuth.DomainLogin, user.Name);
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes()).SetResources(settings.Resource);
        identity.SetDestinations(_ => [Destinations.AccessToken]);
        var authorization = await authorizations.CreateAsync(principal, sid, (await applications.GetIdAsync(application))!,
            AuthorizationTypes.Permanent, principal.GetScopes(), context.RequestAborted);
        principal.SetAuthorizationId(await authorizations.GetIdAsync(authorization));
        store.Audit(user.Name, "mcp.authorized", $"Пользователь подтвердил доступ клиента {request.ClientId}; права {string.Join(", ", principal.GetScopes())}. Ожидается обмен кода на токен.");
        return Results.SignIn(principal, authenticationScheme: Scheme);
    }

    private static async Task<IResult> Exchange(HttpContext context, Store store, IConfiguration configuration, McpConfiguration settings)
    {
        var request = context.GetOpenIddictServerRequest()!;
        var result = await context.AuthenticateAsync(Scheme);
        if (!result.Succeeded || result.Principal is null) return OAuthError(Errors.InvalidGrant, "Недействительный код или refresh token.");
        if (McpOAuth.DomainUser(result.Principal, store, configuration) is not { } user) return OAuthError(Errors.InvalidGrant, "Доменный доступ отключён.");
        if (request.GetResources().Any(x => x != settings.Resource)) return OAuthError(Errors.InvalidTarget, "Токен предназначен только для этого MCP.");
        var action = request.IsAuthorizationCodeGrantType() ? "mcp.token.issued" : "mcp.token.refreshed";
        context.Items[typeof(McpTokenAudit)]=new McpTokenAudit(user.Name,action,request.ClientId??"");
        return Results.SignIn(result.Principal, authenticationScheme: Scheme);
    }

    private static async Task<IResult> Connections(HttpContext context, Store store, SessionService sessions,
        IOpenIddictAuthorizationManager authorizations, IOpenIddictApplicationManager applications, McpConfiguration settings)
    {
        var user = sessions.Current(context)!;
        var sid = user.Kind == "domain" ? WindowsSid(context.User) : null;
        var connections = new List<object>();
        if (sid is not null)
            await foreach (var grant in authorizations.FindBySubjectAsync(sid, context.RequestAborted))
            {
                if (await authorizations.GetStatusAsync(grant) != Statuses.Valid) continue;
                var applicationId = await authorizations.GetApplicationIdAsync(grant);
                var client = applicationId is null ? null : await applications.FindByIdAsync(applicationId);
                connections.Add(new
                {
                    Id = await authorizations.GetIdAsync(grant),
                    Client = client is null ? "MCP client" : await applications.GetDisplayNameAsync(client),
                    CreatedAt = await authorizations.GetCreationDateAsync(grant),
                    Scopes = await authorizations.GetScopesAsync(grant)
                });
            }
        return Results.Ok(new
        {
            Enabled = true,
            Url = settings.Resource,
            DomainConfirmed = sid is not null,
            Connections = connections,
            AccessTokenMinutes = 10,
            RefreshTokenDays = 14
        });
    }

    private static async Task<IResult> Revoke(string id, HttpContext context, Store store, SessionService sessions, IOpenIddictAuthorizationManager authorizations)
    {
        var user = sessions.Current(context)!;
        var sid = user.Kind == "domain" ? WindowsSid(context.User) : null;
        var grant = await authorizations.FindByIdAsync(id);
        if (sid is null || grant is null || await authorizations.GetSubjectAsync(grant) != sid) return Results.NotFound();
        await authorizations.TryRevokeAsync(grant);
        store.Audit(user.Name, "mcp.revoked", $"Подключение {id} отозвано.");
        return Results.NoContent();
    }
}
