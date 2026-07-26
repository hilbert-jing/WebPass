using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using WebPass.Web.Configuration;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.UnitTests.Secrets;

public sealed class CertificateKeyWrapperSecurityTests
{
    [Fact]
    public void Tampered_wrapped_key_is_rejected()
    {
        using var certificate = CreateCertificate();
        var wrapper = NewWrapper(certificate, certificate.Thumbprint);
        var wrapped = wrapper.WrapKey(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        wrapped[^1] ^= 0x01;

        Assert.Throws<CryptographicException>(
            () => wrapper.UnwrapKey(wrapped, certificate.Thumbprint));
    }

    [Fact]
    public void Blank_thumbprint_fails_before_certificate_lookup()
    {
        using var certificate = CreateCertificate();
        var wrapper = NewWrapper(certificate, string.Empty);

        Assert.Throws<InvalidOperationException>(
            () => wrapper.WrapKey(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()));
    }

    private static CertificateKeyWrapper NewWrapper(X509Certificate2 certificate, string thumbprint) => new(
        new FixedCertificateProvider(certificate),
        Options.Create(new SecretEncryptionOptions { CertificateThumbprint = thumbprint }));

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=WebPass Security Test Data Encryption",
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
            return loaded;
        }
    }
}
