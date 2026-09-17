using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Sellora.Api.Authorization;
using Sellora.Domain.Auth;

namespace Sellora.Tests.Authorization;

public class PermissionAuthorizationHandlerTests
{
    private static AuthorizationHandlerContext BuildContext(PermissionRequirement requirement, params string[] permissionCodes)
    {
        var claims = permissionCodes.Select(code => new Claim(AuthClaimTypes.Permission, code));
        var identity = new ClaimsIdentity(claims, "test");
        var principal = new ClaimsPrincipal(identity);
        return new AuthorizationHandlerContext([requirement], principal, resource: null);
    }

    [Fact]
    public async Task HandleRequirementAsync_UserHasPermission_Succeeds()
    {
        var handler = new PermissionAuthorizationHandler();
        var requirement = new PermissionRequirement("Users.View");
        var context = BuildContext(requirement, "Users.View", "Sales.Create");

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_UserMissingPermission_DoesNotSucceed()
    {
        var handler = new PermissionAuthorizationHandler();
        var requirement = new PermissionRequirement("Users.View");
        var context = BuildContext(requirement, "Sales.Create");

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }
}
