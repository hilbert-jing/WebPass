namespace WebPass.Web.Application.Secrets;

public sealed class SecretEnvelope
{
    public SecretEnvelope(byte[] ciphertext, byte[] nonce, byte[] authenticationTag, int keyVersion)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(authenticationTag);

        Ciphertext = ciphertext.ToArray();
        Nonce = nonce.ToArray();
        AuthenticationTag = authenticationTag.ToArray();
        KeyVersion = keyVersion;
    }

    public byte[] Ciphertext { get; }

    public byte[] Nonce { get; }

    public byte[] AuthenticationTag { get; }

    public int KeyVersion { get; }
}
