using System.Security.Cryptography.X509Certificates;

namespace DevRefresh;

public static class DeploymentCertificate
{
    public static X509Certificate2 Load(IConfiguration configuration)
    {
        var thumbprint = configuration["HttpsCertificate:Thumbprint"]?.Replace(" ", "").Trim();
        if (string.IsNullOrEmpty(thumbprint) || thumbprint.Length != 40 || !thumbprint.All(Uri.IsHexDigit))
            throw new InvalidOperationException("HTTPS: задайте 40-символьный SHA-1 отпечаток установленного сертификата.");
        if (!Enum.TryParse<StoreLocation>(configuration["HttpsCertificate:StoreLocation"] ?? "LocalMachine", true, out var location)
            || !Enum.IsDefined(location))
            throw new InvalidOperationException("HTTPS: StoreLocation должен быть LocalMachine или CurrentUser.");
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        var certificate = matches.Cast<X509Certificate2>().FirstOrDefault(x => x.HasPrivateKey);
        if (certificate is null)
            throw new InvalidOperationException($"HTTPS: сертификат {thumbprint} с закрытым ключом не найден в {location}\\My.");
        if (DateTime.Now < certificate.NotBefore || DateTime.Now >= certificate.NotAfter)
            throw new InvalidOperationException("HTTPS: установленный сертификат ещё не действует или уже истёк.");
        return certificate;
    }
}
