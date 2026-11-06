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
    [InlineData(typeof(StockAdjustmentsController), "List", "Inventory.View")]
    [InlineData(typeof(StockAdjustmentsController), "Get", "Inventory.View")]
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

    [Theory]
    [InlineData(typeof(ProductsController), "PreviewUnits", "Inventory.View", "POST", "unit-preview")]
    [InlineData(typeof(ProductsController), "Create", "Inventory.Create", "POST", null)]
    [InlineData(typeof(ProductsController), "Update", "Inventory.Edit", "PUT", "{id:guid}")]
    [InlineData(typeof(ProductsController), "Get", "Inventory.View", "GET", "{id:guid}")]
    [InlineData(typeof(PricingController), "List", "Inventory.Price", "GET", null)]
    [InlineData(typeof(PricingController), "History", "Inventory.Price", "GET", "{productUnitId:guid}/history")]
    [InlineData(typeof(PricingController), "SetPrice", "Inventory.Price", "PUT", "{productUnitId:guid}")]
    [InlineData(typeof(SalesController), "Catalog", "Sales.Create", "GET", "catalog")]
    [InlineData(typeof(SalesController), "CatalogByBarcode", "Sales.Create", "GET", "catalog/by-barcode/{code}")]
    public void ProductUnitEndpoints_RequireTheirPermissionOnTheirRoute(Type controller, string action, string policy, string verb, string? template)
    {
        var method = controller.GetMethod(action)!;
        var http = method.GetCustomAttributes<HttpMethodAttribute>().Single();

        Assert.Contains(policy, Policies(controller, method));
        Assert.Equal(verb, http.HttpMethods.Single());
        Assert.Equal(template, http.Template);
    }

    /// <summary>Policies combine with AND: a class-level Inventory.Adjust would silently require it on the read endpoints
    /// too, so viewers could not open the adjustment list.</summary>
    [Theory]
    [InlineData("List", "GET", null)]
    [InlineData("Get", "GET", "{id:guid}")]
    public void StockAdjustmentReads_RequireOnlyInventoryView(string action, string verb, string? template)
    {
        var controller = typeof(StockAdjustmentsController);
        var method = controller.GetMethod(action)!;
        var http = method.GetCustomAttributes<HttpMethodAttribute>().Single();

        Assert.Equal(new[] { "Inventory.View" }, Policies(controller, method));
        Assert.DoesNotContain("Inventory.Adjust", Policies(controller, method));
        Assert.Equal((verb, template), (http.HttpMethods.Single(), http.Template));
        Assert.Equal("api/v1/stock-adjustments", controller.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public void StockAdjustmentCreate_RequiresOnlyInventoryAdjust()
    {
        var controller = typeof(StockAdjustmentsController);
        Assert.Empty(controller.GetCustomAttributes<AuthorizeAttribute>(true));
        Assert.Equal(new[] { "Inventory.Adjust" }, Policies(controller, controller.GetMethod("Create")!));
    }

    [Fact]
    public void PricingRoutes_AreKeyedByProductUnitId()
    {
        var parameters = typeof(PricingController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any(a => a.Template?.Contains('{') == true))
            .Select(m => m.GetParameters()[0].Name)
            .ToList();

        Assert.NotEmpty(parameters);
        Assert.All(parameters, p => Assert.Equal("productUnitId", p));
        Assert.Equal("api/v1/pricing", typeof(PricingController).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("api/v1/products", typeof(ProductsController).GetCustomAttribute<RouteAttribute>()!.Template);
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
