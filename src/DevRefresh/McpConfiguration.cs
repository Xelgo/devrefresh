using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;

namespace DevRefresh;

public sealed record McpConfiguration(bool Enabled, Uri Issuer, string[] AllowedRedirectUris)
{
    public const string ReadScope = "devrefresh.read";
    public const string RestoreScope = "devrefresh.restore";
    public string Resource => new Uri(Issuer, "mcp").AbsoluteUri;
    public string Metadata => new Uri(Issuer, ".well-known/oauth-protected-resource/mcp").AbsoluteUri;

    public static McpConfiguration Load(IConfiguration configuration)
    {
        var enabled = configuration.GetValue("Mcp:Enabled", false);
        var raw = configuration["Mcp:PublicUrl"];
        var valid = Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0;
        if (enabled && !valid) throw new InvalidOperationException("Для MCP задайте Mcp:PublicUrl — внешний HTTPS-адрес службы без пути, например https://devrefresh.example.com.");
        var result = new McpConfiguration(enabled, valid ? uri! : new Uri("https://localhost/"),
            configuration.GetSection("Mcp:AllowedRedirectUris").Get<string[]>() ?? []);
        if (result.AllowedRedirectUris.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var candidate)
            || candidate.Scheme != "https" || candidate.UserInfo.Length != 0 || candidate.Fragment.Length != 0))
            throw new InvalidOperationException("Mcp:AllowedRedirectUris должен содержать точные HTTPS callback URL без фрагментов и учётных данных.");
        return result;
    }

    public bool AllowsRedirect(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        // Native clients use a loopback HTTP listener with a randomly selected port.
        if (uri.Scheme == "http" && uri.Host is "127.0.0.1" or "[::1]" or "localhost") return true;
        return uri.Scheme == "https" && AllowedRedirectUris.Contains(value, StringComparer.Ordinal);
    }
}

public sealed class McpKeys : IDisposable
{
    private readonly RSA signing = RSA.Create(3072), encryption = RSA.Create(3072);
    public RsaSecurityKey Signing { get; }
    public RsaSecurityKey Encryption { get; }
    public McpKeys(IDataProtectionProvider protection, IConfiguration configuration, IHostEnvironment environment)
    {
        var directory = Path.GetFullPath(configuration["DataDirectory"] ?? "App_Data", environment.ContentRootPath);
        var path = Path.Combine(directory, "mcp-keys.protected");
        var protector = protection.CreateProtector("DevRefresh.Mcp.Keys.v1");
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Файл ключей MCP является ссылкой.");
            var values = JsonSerializer.Deserialize<string[]>(protector.Unprotect(File.ReadAllText(path)))!;
            signing.ImportPkcs8PrivateKey(Convert.FromBase64String(values[0]), out _);
            encryption.ImportPkcs8PrivateKey(Convert.FromBase64String(values[1]), out _);
        }
        else
        {
            var encoded = protector.Protect(JsonSerializer.Serialize(new[]{Convert.ToBase64String(signing.ExportPkcs8PrivateKey()),
                Convert.ToBase64String(encryption.ExportPkcs8PrivateKey())}));
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file); writer.Write(encoded);
        }
        Signing = new(signing) { KeyId = Convert.ToHexString(SHA256.HashData(signing.ExportSubjectPublicKeyInfo())) };
        Encryption = new(encryption) { KeyId = Convert.ToHexString(SHA256.HashData(encryption.ExportSubjectPublicKeyInfo())) };
    }
    public void Dispose() { signing.Dispose(); encryption.Dispose(); }
}
