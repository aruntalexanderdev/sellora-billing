using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Sellora.Api.Controllers;

namespace Sellora.Tests.Api;

/// <summary>
/// Backend authorization is what actually protects stock and batch data — the Angular permission checks only hide
/// buttons. These tests read the controllers themselves, so a new endpoint added without a policy fails the build
/// instead of shipping open.
/// </summary>
public class EndpointAuthorizationTests
{
    private static readonly HashSet<string> Public = ["AuthController"];

    private static IEnumerable<(Type Controller, MethodInfo Action, string Verb)> Actions() =>
        typeof(PurchasesController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => (Controller: t, Action: m, Verb: m.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).FirstOrDefault()))
                .Where(x => x.Verb is not null)
                .Select(x => (x.Controller, x.Action, x.Verb!)));

    private static string[] Policies(Type controller, MethodInfo action) =>
        controller.GetCustomAttributes<AuthorizeAttribute>(true).Concat(action.GetCustomAttributes<AuthorizeAttribute>(true))
            .Select(a => a.Policy ?? "").ToArray();

    // Endpoints any signed-in user may call: their own account, and lookup/dashboard data. Everything else needs
    // a named permission. Adding to this list should be a deliberate decision.
    private static readonly HashSet<string> SignedInOnly =
    [
        "CurrenciesController.List", "DashboardController.Get", "ExchangeRatesController.Resolve",
        "FinancialAccountsController.Options", "UsersController.UpdateOwnUsername", "UsersController.ChangeOwnPassword",
    ];

    private static IEnumerable<(Type Controller, MethodInfo Action, string Verb)> Protected() =>
        Actions().Where(x => !Public.Contains(x.Controller.Name) && !x.Action.GetCustomAttributes<AllowAnonymousAttribute>().Any());

    [Fact]
    public void EveryEndpointOutsideAuth_RequiresSignIn()
    {
        var anonymous = Protected()
            .Where(x => Policies(x.Controller, x.Action).Length == 0)
            .Select(x => $"{x.Verb} {x.Controller.Name}.{x.Action.Name}")
            .ToList();

        Assert.True(anonymous.Count == 0, "Endpoints with no [Authorize]: " + string.Join(", ", anonymous));
    }

    [Fact]
    public void EndpointsWithoutANamedPermission_AreOnTheSignedInOnlyAllowlist()
    {
        var unlisted = Protected()
            .Where(x => !Policies(x.Controller, x.Action).Any(p => p.Length > 0))
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .Where(name => !SignedInOnly.Contains(name))
            .ToList();

        Assert.True(unlisted.Count == 0, "Signed-in-only endpoints not on the allowlist: " + string.Join(", ", unlisted));
    }

    [Theory]
    [InlineData(typeof(PurchasesController), "Create", "Purchase.Create")]
    [InlineData(typeof(PurchasesController), "Cancel", "Purchase.Cancel")]
    [InlineData(typeof(SalesController), "Create", "Sales.Create")]
    [InlineData(typeof(SalesController), "Cancel", "Sales.Cancel")]
    [InlineData(typeof(StockAdjustmentsController), "Create", "Inventory.Adjust")]
    [InlineData(typeof(StockController), "BatchConsumptions", "Sales.View")]
    [InlineData(typeof(StockController), "Detail", "Inventory.View")]
    public void StockAndBatchEndpoints_RequireTheirSpecificPermission(Type controller, string action, string policy)
    {
        var method = controller.GetMethod(action)!;
        Assert.Contains(policy, Policies(controller, method));
    }

    [Theory]
    [InlineData("List", "Masters.View")]
    [InlineData("Get", "Masters.View")]
    [InlineData("Create", "Masters.Edit")]
    [InlineData("Update", "Masters.Edit")]
    [InlineData("SetActive", "Masters.Edit")]
    public void UnitEndpoints_RequireMastersPermission(string action, string policy)
    {
        var method = typeof(UnitsController).GetMethod(action)!;
        Assert.Contains(policy, Policies(typeof(UnitsController), method));
    }

    [Fact]
    public void NoEndpointLetsAClientWriteBatchesDirectly()
    {
        // Batches are created only by purchases and stock adjustments; there must be no route that mutates them.
        var writers = Actions()
            .Where(x => x.Verb is "POST" or "PUT" or "PATCH" or "DELETE")
            .Where(x => x.Controller.Name.Contains("Batch", StringComparison.OrdinalIgnoreCase) || x.Action.Name.Contains("Batch", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Verb} {x.Controller.Name}.{x.Action.Name}")
            .ToList();

        Assert.Empty(writers);
    }
}
