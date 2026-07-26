using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using WebPass.Web.Configuration;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.UnitTests.Secrets;

public sealed class CertificateKeyWrapperTests
{
    [Fact]
    public void Wrap_then_unwrap_returns_original_data_key()
    {
        using var certificate = CreateCertificate();
        var provider = new FixedCertificateProvider(certificate);
        var wrapper = new CertificateKeyWrapper(
            provider,
            Options.Create(new SecretEncryptionOptions
            {
                CertificateThumbprint = certificate.Thumbprint,
            }));
        var dataKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

        var wrapped = wrapper.WrapKey(dataKey);
        var unwrapped = wrapper.UnwrapKey(wrapped, certificate.Thumbprint);

        Assert.Equal(dataKey, unwrapped);
        Assert.NotEqual(dataKey, wrapped);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=WebPass Unit Test Data Encryption",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class FixedCertificateProvider(X509Certificate2 certificate) : ICertificateProvider
    {
        private readonly byte[] _pfx = certificate.Export(X509ContentType.Pfx);

        public X509Certificate2 GetByThumbprint(string thumbprint, bool requirePrivateKey)
        {
            var loaded = X509CertificateLoader.LoadPkcs12(_pfx, null);
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    loaded.Thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal),
                    thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal)))
            {
                loaded.Dispose();
                throw new CryptographicException("Certificate not found.");
            }

            if (requirePrivateKey && !loaded.HasPrivateKey)
            {
                loaded.Dispose();
                throw new CryptographicException("Certificate private key is unavailable.");
            }

            return loaded;
        }
    }
}
