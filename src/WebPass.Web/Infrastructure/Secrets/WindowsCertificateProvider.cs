using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WebPass.Web.Infrastructure.Secrets;

public sealed class WindowsCertificateProvider : ICertificateProvider
{
    public X509Certificate2 GetByThumbprint(string thumbprint, bool requirePrivateKey)
    {
        var normalized = CertificateKeyWrapper.NormalizeThumbprint(thumbprint);
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException("The local-machine certificate store is unavailable.", exception);
        }

        var certificate = store.Certificates
            .OfType<X509Certificate2>()
            .FirstOrDefault(candidate => StringComparer.Ordinal.Equals(
                CertificateKeyWrapper.NormalizeThumbprint(candidate.Thumbprint),
                normalized))
            ?? throw new CryptographicException("The configured data-encryption certificate was not found.");

        if (requirePrivateKey && !certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new CryptographicException("The data-encryption certificate private key is unavailable.");
        }

        return certificate;
    }
}
