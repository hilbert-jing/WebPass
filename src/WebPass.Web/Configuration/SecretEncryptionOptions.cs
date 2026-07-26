namespace WebPass.Web.Configuration;

public sealed class SecretEncryptionOptions
{
    public const string SectionName = "SecretEncryption";

    public string CertificateThumbprint { get; init; } = string.Empty;
}
