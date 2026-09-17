using Sellora.Infrastructure.Security;

namespace Sellora.Tests.Security;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        var hash = _hasher.Hash("Passw0rd!23");
        Assert.True(_hasher.Verify("Passw0rd!23", hash));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = _hasher.Hash("Passw0rd!23");
        Assert.False(_hasher.Verify("something-else", hash));
    }

    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentHashes()
    {
        var hash1 = _hasher.Hash("Passw0rd!23");
        var hash2 = _hasher.Hash("Passw0rd!23");
        Assert.NotEqual(hash1, hash2);
    }
}
