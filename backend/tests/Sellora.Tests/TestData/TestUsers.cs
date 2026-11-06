using Sellora.Domain.Entities;

namespace Sellora.Tests.TestData;

/// <summary>Users for service-test fixtures. Each call returns new instances (the user carries its role navigation,
/// so adding the user adds the role).</summary>
public static class TestUsers
{
    public static User Admin(string username = "admin")
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        return new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = username,
            Email = $"{username}@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
    }
}
