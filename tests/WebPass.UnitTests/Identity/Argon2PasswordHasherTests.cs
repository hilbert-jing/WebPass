using WebPass.Web.Infrastructure.Identity;
using Xunit;

namespace WebPass.UnitTests.Identity;

public sealed class Argon2PasswordHasherTests
{
    [Fact]
    public void Verifies_the_correct_password_and_rejects_a_different_password()
    {
        var hasher = new Argon2PasswordHasher();
        var hash = hasher.Hash("correct-password");

        Assert.True(hasher.Verify("correct-password", hash));
        Assert.False(hasher.Verify("wrong-password", hash));
    }
}
