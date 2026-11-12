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
        "BranchesController.Accessible", "CurrenciesController.List", "DashboardController.Get", "ExchangeRatesController.Resolve",
        "FinancialAccountsController.Options", "UsersController.UpdateOwnUsername", "UsersController.ChangeOwnPassword",
        // Phase 7: scoped roles. A branch administrator holds Branch.Roles.* / Branch.Users.AssignRoles at ONE branch only, so a named company-level policy
        // would lock them out. These endpoints are plain [Authorize] and every action checks the caller's effective permissions at the role's scope
        // (RoleRules, R1-R4; 403 role_management_forbidden / role_assignment_forbidden, 404 outside reach). ScopedRolesApiTests.EveryServiceCheckedRoleRoute_RefusesASignedInUserWithoutAnyCode proves each route refuses a signed-in user with no codes.
        "RolesController.List", "RolesController.Get", "RolesController.Create", "RolesController.Update", "RolesController.Duplicate", "RolesController.SetActive",
        "RoleAssignmentsController.Get", "RoleAssignmentsController.Assign", "RoleAssignmentsController.End", "RoleAssignmentsController.Members",
    ];

    /// <summary>The Phase 7 role routes that rely on service-level scope checks instead of a named policy (kept in sync with <see cref="SignedInOnly"/> by <see cref="RoleEndpoints_AreExactlyTheServiceCheckedRoutes"/>).</summary>
    private static readonly string[] ServiceCheckedRoleRoutes =
    [
        "GET RoleAssignmentsController.Get", "GET RoleAssignmentsController.Members", "GET RolesController.Get", "GET RolesController.List",
        "POST RoleAssignmentsController.Assign", "POST RoleAssignmentsController.End", "POST RolesController.Create", "POST RolesController.Duplicate",
        "POST RolesController.SetActive", "PUT RolesController.Update",
    ];

    [Fact]
    public void RoleEndpoints_AreExactlyTheServiceCheckedRoutes_AllBehindSignIn()
    {
        var actual = Protected()
            .Where(x => x.Controller == typeof(RolesController) || x.Controller == typeof(RoleAssignmentsController))
            .ToList();

        Assert.Equal(ServiceCheckedRoleRoutes, actual.Select(x => $"{x.Verb} {x.Controller.Name}.{x.Action.Name}").Order().ToArray());
        foreach (var (controller, action, _) in actual)
            Assert.Contains(Policies(controller, action), p => p.Length == 0); // plain [Authorize]: never anonymous
    }

    [Theory]
    [InlineData("Units", "Create")]
    [InlineData("Units", "Update")]
    [InlineData("Units", "SetActive")]
    [InlineData("Currencies", "Create")]
    [InlineData("Currencies", "Update")]
    [InlineData("Currencies", "SetActive")]
    public void CompanyReferenceDataWrites_StillRequireMastersEdit_AndAreNotPlatformEndpoints(string controllerName, string action)
    {
        var controller = controllerName == "Units" ? typeof(UnitsController) : typeof(CurrenciesController);

        Assert.Contains("Masters.Edit", Policies(controller, controller.GetMethod(action)!));
        Assert.False(IsPlatformEndpoint(controller, controller.GetMethod(action)!));
    }

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
    [InlineData(typeof(BranchesController), "List", "Company.Branches.View")]
    [InlineData(typeof(BranchesController), "Get", "Company.Branches.View")]
    [InlineData(typeof(BranchesController), "Create", "Company.Branches.Create")]
    [InlineData(typeof(BranchesController), "Update", "Company.Branches.Edit")]
    [InlineData(typeof(BranchesController), "SetStatus", "Company.Branches.Disable")]
    public void BranchEndpoints_RequireTheirBranchPermission(Type controller, string action, string policy)
    {
        Assert.Contains(policy, Policies(controller, controller.GetMethod(action)!));
    }

    [Theory]
    [InlineData(typeof(WarehousesController), "List", "Company.Warehouses.View")]
    [InlineData(typeof(WarehousesController), "Get", "Company.Warehouses.View")]
    [InlineData(typeof(WarehousesController), "Create", "Company.Warehouses.Create")]
    [InlineData(typeof(WarehousesController), "Update", "Company.Warehouses.Edit")]
    [InlineData(typeof(WarehousesController), "SetStatus", "Company.Warehouses.Disable")]
    [InlineData(typeof(VehiclesController), "List", "Company.Vehicles.View")]
    [InlineData(typeof(VehiclesController), "Get", "Company.Vehicles.View")]
    [InlineData(typeof(VehiclesController), "Create", "Company.Vehicles.Create")]
    [InlineData(typeof(VehiclesController), "Update", "Company.Vehicles.Edit")]
    [InlineData(typeof(VehiclesController), "SetStatus", "Company.Vehicles.Disable")]
    public void WarehouseAndVehicleEndpoints_RequireExactlyTheirOwnPermission(Type controller, string action, string policy)
    {
        var policies = Policies(controller, controller.GetMethod(action)!).Where(p => p.Length > 0).ToList();

        Assert.Equal([policy], policies);
    }

    [Theory]
    [InlineData(typeof(WarehousesController), "api/v1/warehouses", 5)]
    [InlineData(typeof(VehiclesController), "api/v1/vehicles", 5)]
    public void WarehouseAndVehicleControllers_AreRoutedAndHaveExactlyFiveActions(Type controller, string route, int actions)
    {
        var routes = controller.GetCustomAttributes<Microsoft.AspNetCore.Mvc.RouteAttribute>().Select(r => r.Template).ToList();
        var methods = controller.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any()).ToList();

        Assert.Equal([route], routes);
        Assert.Equal(actions, methods.Count);
    }

    [Fact]
    public void WarehouseAndVehiclePermissionCodes_AreInTheCatalogue_AndAdministratorResolvesAllEight()
    {
        string[] codes =
        [
            "Company.Warehouses.View", "Company.Warehouses.Create", "Company.Warehouses.Edit", "Company.Warehouses.Disable",
            "Company.Vehicles.View", "Company.Vehicles.Create", "Company.Vehicles.Edit", "Company.Vehicles.Disable",
        ];
        var catalogue = Sellora.Domain.Permissions.PermissionCatalog.All.Select(p => p.Code).ToHashSet();
        var admin = Sellora.Domain.Auth.PermissionResolver.Resolve(new Sellora.Domain.Entities.Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true, RolePermissions = [] });
        var clerk = Sellora.Domain.Auth.PermissionResolver.Resolve(new Sellora.Domain.Entities.Role { Id = Guid.NewGuid(), Name = "Clerk", IsSystem = false, RolePermissions = [] });

        Assert.Superset(codes.ToHashSet(), catalogue);
        Assert.Superset(codes.ToHashSet(), admin.ToHashSet());
        Assert.DoesNotContain(clerk, c => c.StartsWith("Company.Warehouses.") || c.StartsWith("Company.Vehicles."));
    }

    [Fact]
    public void StockLocationsView_DescriptionMentionsWarehousesAndVehicles()
    {
        var description = Sellora.Domain.Permissions.PermissionCatalog.All.Single(p => p.Code == "StockLocations.View").Description;

        Assert.Contains("warehouse", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vehicle", description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(typeof(StockLocationsController), "List", "StockLocations.View")]
    [InlineData(typeof(StockLocationsController), "Get", "StockLocations.View")]
    public void StockLocationEndpoints_RequireStockLocationsView(Type controller, string action, string policy)
    {
        Assert.Contains(policy, Policies(controller, controller.GetMethod(action)!));
    }

    [Fact]
    public void StockLocationsController_IsReadOnly_NoWriteActions()
    {
        var verbs = typeof(StockLocationsController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().SelectMany(a => a.HttpMethods)).ToList();
        Assert.NotEmpty(verbs);
        Assert.All(verbs, v => Assert.Equal("GET", v));
    }

    [Fact]
    public void StockLocationsView_IsInTheCatalogue()
    {
        Assert.Contains("StockLocations.View", Sellora.Domain.Permissions.PermissionCatalog.All.Select(p => p.Code));
    }

    [Fact]
    public void BranchesAccessible_IsAuthenticatedOnlyBecauseABranchIsADataScopeNotAnAction()
    {
        var method = typeof(BranchesController).GetMethod("Accessible")!;
        Assert.All(Policies(typeof(BranchesController), method).Where(p => p.Length > 0), p => Assert.Fail($"Accessible must not need a named permission, found {p}"));
        Assert.Contains(method.GetCustomAttributes<AuthorizeAttribute>(), _ => true);
    }

    [Fact]
    public void BranchPermissionCodes_AreInTheCatalogue()
    {
        var codes = Sellora.Domain.Permissions.PermissionCatalog.All.Select(p => p.Code).ToHashSet();
        Assert.Superset(new HashSet<string> { "Company.Branches.View", "Company.Branches.Create", "Company.Branches.Edit", "Company.Branches.Disable" }, codes);
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
    [InlineData(typeof(ProductsController), "Delete", "Inventory.Delete", "DELETE", "{id:guid}")]
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

    // ---- Phase 5: user branch access ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Get", "Users.View")]
    [InlineData("Events", "Company.Users.ManageBranchAccess")]
    [InlineData("SetScope", "Company.Users.ManageBranchAccess")]
    [InlineData("AddAssignment", "Company.Users.ManageBranchAccess")]
    [InlineData("EndAssignment", "Company.Users.ManageBranchAccess")]
    [InlineData("SetPrimary", "Company.Users.ManageBranchAccess")]
    [InlineData("Reassign", "Company.Users.ManageBranchAccess")]
    [InlineData("SetStatus", "Company.Users.ManageBranchAccess")]
    public void UserAccessEndpoints_RequireExactlyTheirPermission(string action, string policy)
    {
        var controller = typeof(UserAccessController);

        var policies = Policies(controller, controller.GetMethod(action)!).Where(p => p.Length > 0).ToList();

        Assert.Equal([policy], policies);
    }

    [Fact]
    public void UserAccessController_IsRoutedUnderTheUserAndHasExactlyEightActions()
    {
        var controller = typeof(UserAccessController);
        var routes = controller.GetCustomAttributes<Microsoft.AspNetCore.Mvc.RouteAttribute>().Select(r => r.Template).ToList();
        var methods = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any()).ToList();

        Assert.Equal(["api/v1/users/{id:guid}/access"], routes);
        Assert.Equal(8, methods.Count);
    }

    [Fact]
    public void ManageBranchAccessPermission_IsInTheCatalogue_AdministratorHasIt_OtherRolesDoNot()
    {
        var code = "Company.Users.ManageBranchAccess";
        var admin = Sellora.Domain.Auth.PermissionResolver.Resolve(new Sellora.Domain.Entities.Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true, RolePermissions = [] });
        var clerk = Sellora.Domain.Auth.PermissionResolver.Resolve(new Sellora.Domain.Entities.Role { Id = Guid.NewGuid(), Name = "Clerk", IsSystem = false, RolePermissions = [] });

        Assert.Contains(code, Sellora.Domain.Permissions.PermissionCatalog.All.Select(p => p.Code));
        Assert.Contains(code, admin);
        Assert.DoesNotContain(code, clerk);
    }

    [Fact]
    public void OnlyTheAccountEndpointsAndAuthMe_AreMarkedAllowWithoutBranch()
    {
        var marked = Actions()
            .Where(x => x.Action.GetCustomAttributes<Sellora.Api.Authorization.AllowWithoutBranchAttribute>().Any()
                     || x.Controller.GetCustomAttributes<Sellora.Api.Authorization.AllowWithoutBranchAttribute>().Any())
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .Order().ToList();

        Assert.Equal(["AuthController.Me", "AuthController.SwitchCompany", "UsersController.ChangeOwnPassword", "UsersController.UpdateOwnUsername"], marked);
    }

    // ---- Phase 6: platform separation and governance (T10) ----

    private const string PlatformAdminPolicy = "Platform.Admin";

    private static bool IsPlatformEndpoint(Type controller, MethodInfo action) =>
        controller.GetCustomAttributes<Sellora.Api.Authorization.PlatformEndpointAttribute>(true).Any()
        || action.GetCustomAttributes<Sellora.Api.Authorization.PlatformEndpointAttribute>(true).Any();

    [Fact]
    public void PlatformEndpoints_AreExactlyTheTwelvePlatformRoutes_EachRequiringOnlyPlatformAdmin()
    {
        var platform = Actions().Where(x => IsPlatformEndpoint(x.Controller, x.Action)).ToList();

        Assert.Equal(
            [
                "GET PlatformCompaniesController.Currencies", "GET PlatformCompaniesController.List", "GET PlatformReferenceDataController.Currencies", "GET PlatformReferenceDataController.Units",
                "POST PlatformCompaniesController.Create", "POST PlatformCompaniesController.SetStatus", "POST PlatformReferenceDataController.CreateCurrency",
                "POST PlatformReferenceDataController.CreateUnit", "POST PlatformReferenceDataController.SetCurrencyActive", "POST PlatformReferenceDataController.SetUnitActive",
                "PUT PlatformReferenceDataController.UpdateCurrency", "PUT PlatformReferenceDataController.UpdateUnit",
            ],
            platform.Select(x => $"{x.Verb} {x.Controller.Name}.{x.Action.Name}").Order().ToArray());
        foreach (var (controller, action, _) in platform)
            Assert.Equal([PlatformAdminPolicy], Policies(controller, action).Where(p => p.Length > 0).ToArray());
    }

    [Fact]
    public void PlatformPolicy_IsNeverMixedWithCompanyPermissions_AndCompanyEndpointsAreNeverPlatformEndpoints()
    {
        foreach (var (controller, action, verb) in Actions())
        {
            var policies = Policies(controller, action).Where(p => p.Length > 0).ToArray();
            var isPlatform = IsPlatformEndpoint(controller, action);
            Assert.True(isPlatform == policies.Contains(PlatformAdminPolicy), $"{verb} {controller.Name}.{action.Name}: Platform.Admin and [PlatformEndpoint] must go together.");
            if (isPlatform) Assert.Single(policies);
        }
    }

    [Fact]
    public void PlatformRoutes_LiveOnlyOnPlatformControllers()
    {
        foreach (var controller in typeof(PurchasesController).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var routes = controller.GetCustomAttributes<Microsoft.AspNetCore.Mvc.RouteAttribute>().Select(r => r.Template).ToList();
            if (routes.Any(r => r.StartsWith("api/v1/platform", StringComparison.Ordinal)))
                Assert.True(controller.GetCustomAttributes<Sellora.Api.Authorization.PlatformEndpointAttribute>(true).Any(), $"{controller.Name} serves api/v1/platform but is not a [PlatformEndpoint].");
        }
    }

    [Fact]
    public void PlatformLogin_IsTheOnlyAnonymousEndpointBesidesAuthController()
    {
        var anonymous = Actions().Where(x => x.Action.GetCustomAttributes<AllowAnonymousAttribute>().Any() && x.Controller != typeof(AuthController))
            .Select(x => $"{x.Verb} {x.Controller.Name}.{x.Action.Name}").ToList();

        Assert.Equal(["POST PlatformAuthController.Login"], anonymous);
    }

    [Theory]
    [InlineData("GET", "Permissions", "Company.Governance.View")]
    [InlineData("GET", "Members", "Company.Governance.View")]
    [InlineData("GET", "Events", "Company.Governance.View")]
    [InlineData("PUT", "SetPermissions", "Company.Governance.AssignPermissions")]
    [InlineData("POST", "GrantAdministrator", "Company.Governance.ManageAdministrators")]
    [InlineData("POST", "RemoveAdministrator", "Company.Governance.ManageAdministrators")]
    public void GovernanceEndpoints_RequireTheirGovernancePolicy_NotACompanyPermission(string verb, string action, string code)
    {
        var method = GovernanceActions().Single(x => x.Verb == verb && x.Action.Name.Contains(action, StringComparison.OrdinalIgnoreCase)).Action;

        Assert.Equal(["Governance:" + code], Policies(typeof(GovernanceController), method).Where(p => p.Length > 0).ToArray());
        Assert.DoesNotContain(code, Sellora.Domain.Permissions.PermissionCatalog.All.Select(p => p.Code));
    }

    private static IEnumerable<(string Verb, MethodInfo Action)> GovernanceActions() =>
        Actions().Where(x => x.Controller == typeof(GovernanceController)).Select(x => (x.Verb, x.Action));

    [Fact]
    public void GovernanceController_HasNineRoutedActions_AllGovernancePolicies_AndNoPlatformMarker()
    {
        var routes = typeof(GovernanceController).GetCustomAttributes<Microsoft.AspNetCore.Mvc.RouteAttribute>().Select(r => r.Template).ToList();
        var actions = GovernanceActions().ToList();

        Assert.Equal(["api/v1/governance"], routes);
        Assert.Equal(9, actions.Count);
        foreach (var (_, action) in actions)
        {
            var policies = Policies(typeof(GovernanceController), action).Where(p => p.Length > 0).ToArray();
            Assert.Single(policies);
            Assert.StartsWith("Governance:Company.Governance.", policies[0]);
            Assert.False(IsPlatformEndpoint(typeof(GovernanceController), action));
        }
    }

    [Fact]
    public void GovernanceCodes_AreNeverInThePermissionCatalogue_AndTheSystemAdministratorResolvesNone()
    {
        var governance = Sellora.Domain.Permissions.GovernancePermissionCatalog.All.Select(d => d.Code).ToHashSet();
        var admin = Sellora.Domain.Auth.PermissionResolver.Resolve(new Sellora.Domain.Entities.Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true, RolePermissions = [] });

        Assert.Equal(5, governance.Count);
        Assert.Empty(governance.Intersect(Sellora.Domain.Permissions.PermissionCatalog.All.Select(p => p.Code)));
        Assert.Empty(governance.Intersect(admin));
    }
}

