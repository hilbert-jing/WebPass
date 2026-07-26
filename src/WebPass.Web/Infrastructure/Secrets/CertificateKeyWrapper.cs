using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Configuration;

namespace WebPass.Web.Infrastructure.Secrets;

public sealed class CertificateKeyWrapper(
    ICertificateProvider certificateProvider,
    IOptions<SecretEncryptionOptions> options) : IDataKeyWrapper
{
    public string CurrentCertificateThumbprint => NormalizeThumbprint(options.Value.CertificateThumbprint);

    public byte[] WrapKey(ReadOnlySpan<byte> dataKey)
    {
        if (dataKey.Length != 32) throw new ArgumentException("An AES-256 data key must contain 32 bytes.", nameof(dataKey));

        using var certificate = certificateProvider.GetByThumbprint(CurrentCertificateThumbprint, requirePrivateKey: false);
        using var rsa = certificate.GetRSAPublicKey()
            ?? throw new CryptographicException("The data-encryption certificate does not contain an RSA public key.");
        return rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
    }

    public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey, string certificateThumbprint)
    {
        if (wrappedKey.IsEmpty) throw new ArgumentException("A wrapped data key is required.", nameof(wrappedKey));

        using var certificate = certificateProvider.GetByThumbprint(
            NormalizeThumbprint(certificateThumbprint),
            requirePrivateKey: true);
        using var rsa = certificate.GetRSAPrivateKey()
            ?? throw new CryptographicException("The data-encryption certificate private key is unavailable.");
        return rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
    }

    internal static string NormalizeThumbprint(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            throw new InvalidOperationException("A data-encryption certificate thumbprint is required.");

        var normalized = new string(thumbprint.Where(character => !char.IsWhiteSpace(character)).ToArray())
            .ToUpperInvariant();
        if (normalized.Length is < 40 or > 128 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("The data-encryption certificate thumbprint is invalid.");
        return normalized;
    }
}
