using System.Security.Cryptography;

namespace WebPass.Web.Application.Secrets;

public sealed class DataEncryptionKeyMaterial : IDisposable
{
    private byte[] _key;

    public DataEncryptionKeyMaterial(int keyVersion, byte[] key)
    {
        if (keyVersion <= 0) throw new ArgumentOutOfRangeException(nameof(keyVersion));
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("An AES-256 key must contain 32 bytes.", nameof(key));

        KeyVersion = keyVersion;
        _key = key.ToArray();
    }

    public int KeyVersion { get; }

    public ReadOnlyMemory<byte> Key => _key;

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        _key = [];
    }
}
