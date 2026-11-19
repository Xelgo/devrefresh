namespace DevRefresh;

internal static class BrowserSecurityPolicy
{
    internal const string Default = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";

    internal static string ForOAuthConsent(McpConfiguration configuration, string redirectUri)
    {
        if (!configuration.AllowsRedirect(redirectUri)) throw new InputException("Адрес возврата OAuth не разрешён.");
        // Chromium applies form-action to the redirect following the consent POST as well.
        // Use only a validated URI's origin: paths/queries must never become CSP directives.
        var origin = new Uri(redirectUri).GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        return Default + " " + origin;
    }
}
