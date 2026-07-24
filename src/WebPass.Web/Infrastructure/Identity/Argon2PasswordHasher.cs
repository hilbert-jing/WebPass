using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace WebPass.Web.Infrastructure.Identity;

public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int SaltLength = 16;
    private const int HashLength = 32;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Derive(password, salt);
        return $"argon2id$v1${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedHash);
        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "argon2id" || parts[1] != "v1")
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            return CryptographicOperations.FixedTimeEquals(Derive(password, salt), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = 1,
            Iterations = 3,
            MemorySize = 65536,
        };
        return argon2.GetBytes(HashLength);
    }
}
