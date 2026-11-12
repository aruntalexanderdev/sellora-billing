using Sellora.Api.Contracts;
using Sellora.Domain.Entities;

namespace Sellora.IntegrationTests.Api;

/// <summary>A GET that must show only the caller's company. <paramref name="Detail"/>: the other company's id must answer 404.</summary>
public sealed record ReadCase(string Action, Func<Ids, string> Route, bool Detail = false, bool ShowsRowIds = true, bool EmptyIsNotFound = false);

/// <summary>A mutating request from one company that names the OTHER company's ids; the API must refuse (400/404) and change nothing.</summary>
public sealed record WriteCase(string Action, string Scenario, HttpMethod Method, Func<Ids, Ids, (string Route, object? Body)> Build);

/// <summary>
/// The isolation table (design T6). Every controller action must appear here or in <see cref="PlatformActions"/> /
/// <see cref="CoveredElsewhere"/>; <c>CompanyIsolationApiTests.EveryControllerAction_IsInTheIsolationTableOrPlatformAllowlist</c> enforces it,
/// so a new endpoint cannot ship without an isolation decision.
/// </summary>
public static class IsolationCases
{
    /// <summary>Set on the pen base unit of BOTH companies by <see cref="IsolationWorld"/>.</summary>
    public const string SharedBarcode = "8901234500001";

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public static readonly ReadCase[] Reads =
    [
        new("BranchesController.List", _ => "/api/v1/branches?includeInactive=true"),
        new("BranchesController.Get", i => $"/api/v1/branches/{i.BranchId}", Detail: true),
        new("BranchesController.Accessible", _ => "/api/v1/branches/accessible"),
        new("StockLocationsController.List", _ => "/api/v1/stock-locations?includeInactive=true"),
        new("StockLocationsController.Get", i => $"/api/v1/stock-locations/{i.LocationId}", Detail: true),
        new("WarehousesController.List", _ => "/api/v1/warehouses?includeInactive=true"),
        new("WarehousesController.Get", i => $"/api/v1/warehouses/{i.WarehouseId}", Detail: true),
        new("VehiclesController.List", _ => "/api/v1/vehicles?includeInactive=true"),
        new("VehiclesController.Get", i => $"/api/v1/vehicles/{i.VehicleId}", Detail: true),
        new("ProductsController.List", _ => "/api/v1/products"),
        new("ProductsController.Get", i => $"/api/v1/products/{i.ProductId}", Detail: true),
        new("SuppliersController.List", _ => "/api/v1/suppliers"),
        new("SuppliersController.Get", i => $"/api/v1/suppliers/{i.SupplierId}", Detail: true),
        new("CustomersController.List", _ => "/api/v1/customers"),
        new("CustomersController.Get", i => $"/api/v1/customers/{i.CustomerId}", Detail: true),
        new("TaxesController.List", _ => "/api/v1/taxes"),
        new("ExpenseCategoriesController.List", _ => "/api/v1/expense-categories"),
        new("ExpensesController.List", _ => "/api/v1/expenses"),
        new("ExpensesController.Get", i => $"/api/v1/expenses/{i.ExpenseId}", Detail: true),
        new("FinancialAccountsController.List", _ => "/api/v1/financial-accounts"),
        new("FinancialAccountsController.Options", _ => "/api/v1/financial-accounts/options"),
        new("FinancialAccountsController.Get", i => $"/api/v1/financial-accounts/{i.AccountId}", Detail: true),
        new("FinancialTransactionsController.List", _ => "/api/v1/financial-transactions", ShowsRowIds: false), // items are empty today: see FinancialTransactionsList_ItemsMatchTheReportedTotal
        new("PurchasesController.List", _ => "/api/v1/purchases"),
        new("PurchasesController.Get", i => $"/api/v1/purchases/{i.PurchaseId}", Detail: true),
        new("PurchaseReturnsController.List", _ => "/api/v1/purchase-returns"),
        new("PurchaseReturnsController.Get", i => $"/api/v1/purchase-returns/{i.PurchaseReturnId}", Detail: true),
        new("SalesController.List", _ => "/api/v1/sales"),
        new("SalesController.Get", i => $"/api/v1/sales/{i.SaleId}", Detail: true),
        new("SalesController.Catalog", _ => "/api/v1/sales/catalog"),
        new("SalesController.CatalogByBarcode", _ => $"/api/v1/sales/catalog/by-barcode/{SharedBarcode}"),
        new("SalesReturnsController.List", _ => "/api/v1/sales-returns"),
        new("SalesReturnsController.Get", i => $"/api/v1/sales-returns/{i.SalesReturnId}", Detail: true),
        new("StockAdjustmentsController.List", _ => "/api/v1/stock-adjustments"),
        new("StockAdjustmentsController.Get", i => $"/api/v1/stock-adjustments/{i.AdjustmentId}", Detail: true),
        new("ExchangeRatesController.List", _ => "/api/v1/exchange-rates"),
        new("ExchangeRatesController.Get", i => $"/api/v1/exchange-rates/{i.ExchangeRateId}", Detail: true),
        new("PricingController.List", _ => "/api/v1/pricing"),
        new("PricingController.History", i => $"/api/v1/pricing/{i.ProductUnitId}/history", Detail: true, EmptyIsNotFound: true),
        new("StockController.Overview", _ => "/api/v1/stock/overview"),
        new("StockController.Detail", i => $"/api/v1/stock/{i.ProductId}", Detail: true),
        new("StockController.BatchConsumptions", i => $"/api/v1/stock/batches/{i.BatchId}/consumptions", Detail: true),
        new("SettingsController.Get", _ => "/api/v1/settings"),
        new("DashboardController.Get", _ => "/api/v1/dashboard", ShowsRowIds: false),
        new("ReportsController.CurrentStock", _ => "/api/v1/reports/current-stock"),
        new("ReportsController.CurrentStockCategories", _ => "/api/v1/reports/current-stock/categories", ShowsRowIds: false),
        new("ReportsController.StockMovement", _ => "/api/v1/reports/stock-movement"),
        new("ReportsController.Purchases", _ => "/api/v1/reports/purchases"),
        new("ReportsController.Sales", _ => "/api/v1/reports/sales"),
        new("ReportsController.ProfitSummary", _ => "/api/v1/reports/profit-summary", ShowsRowIds: false),
        new("AccountsReportsController.PurchasePayments", _ => "/api/v1/reports/accounts/purchase-payments"),
        new("AccountsReportsController.SalesPayments", _ => "/api/v1/reports/accounts/sales-payments", ShowsRowIds: false),
        new("AccountsReportsController.SupplierOutstanding", _ => "/api/v1/reports/accounts/supplier-outstanding"),
        new("AccountsReportsController.CustomerOutstanding", _ => "/api/v1/reports/accounts/customer-outstanding"),
        new("AccountsReportsController.Expenses", _ => "/api/v1/reports/accounts/expenses"),
        new("AccountsReportsController.CashFlow", _ => "/api/v1/reports/accounts/cash-flow", ShowsRowIds: false),
        new("AccountsReportsController.CashBank", _ => "/api/v1/reports/accounts/cash-bank"),
        new("AccountsReportsController.PaymentMethods", _ => "/api/v1/reports/accounts/payment-methods", ShowsRowIds: false),
        new("GovernanceController.Members", _ => "/api/v1/governance/members?includeEnded=true"),
        new("GovernanceController.Events", _ => "/api/v1/governance/events?pageSize=100", ShowsRowIds: false),
        // Phase 7: roles and assignments are company-owned.
        new("RolesController.List", _ => "/api/v1/roles"),
        new("RolesController.Get", i => $"/api/v1/roles/{i.RoleId}", Detail: true),
        new("RoleAssignmentsController.Get", i => $"/api/v1/users/{i.UserId}/roles", Detail: true),
        new("RoleAssignmentsController.Members", i => $"/api/v1/branches/{i.BranchId}/members", Detail: true, ShowsRowIds: false),
        new("ExchangeRatesController.Resolve", i => $"/api/v1/exchange-rates/resolve?currencyId={i.CurrencyId}&date={Today:yyyy-MM-dd}", ShowsRowIds: false),
    ];

    private static UpsertProductUnitRequest EachUnit(Ids i) =>
        new(null, i.BaseUnitId, null, 1m, "Each", null);

    private static CreatePurchaseLineRequest PurchaseLine(Ids product) =>
        new(product.ProductId, product.ProductUnitId, 1m, 5m, 0, 0);

    private static CreateSaleLineRequest SaleLine(Ids product) =>
        new(product.ProductId, product.ProductUnitId, 1m, 10m, 0);

    private static HttpMethod Post => HttpMethod.Post;
    private static HttpMethod Put => HttpMethod.Put;

    public static readonly WriteCase[] Writes =
    [
        new("BranchesController.Update", "other company's branch", Put, (own, other) =>
            ($"/api/v1/branches/{other.BranchId}", new UpdateBranchRequest("Hijacked", null, null, null, null, null, null, null))),
        new("BranchesController.SetStatus", "other company's branch", Post, (own, other) =>
            ($"/api/v1/branches/{other.BranchId}/status", new SetStatusRequest(false, "test"))),
        new("WarehousesController.Update", "other company's warehouse", Put, (own, other) =>
            ($"/api/v1/warehouses/{other.WarehouseId}", new UpdateWarehouseRequest("Hijacked"))),
        new("WarehousesController.SetStatus", "other company's warehouse", Post, (own, other) =>
            ($"/api/v1/warehouses/{other.WarehouseId}/status", new SetStatusRequest(false, "test"))),
        new("WarehousesController.Create", "other company's branch", Post, (own, other) =>
            ("/api/v1/warehouses", new CreateWarehouseRequest("ISO-X", "Cross branch", other.BranchId))),
        new("VehiclesController.Update", "other company's vehicle", Put, (own, other) =>
            ($"/api/v1/vehicles/{other.VehicleId}", new UpdateVehicleRequest("Hijacked", "Van", null))),
        new("VehiclesController.Update", "own vehicle assigned to other company's branch", Put, (own, other) =>
            ($"/api/v1/vehicles/{own.VehicleId}", new UpdateVehicleRequest("Isolation van", "Van", other.BranchId))),
        new("VehiclesController.SetStatus", "other company's vehicle", Post, (own, other) =>
            ($"/api/v1/vehicles/{other.VehicleId}/status", new SetStatusRequest(false, "test"))),
        new("VehiclesController.Create", "other company's branch", Post, (own, other) =>
            ("/api/v1/vehicles", new CreateVehicleRequest("ISO-X", "Cross branch", "Van", other.BranchId))),
        new("ProductsController.Create", "other company's tax", Post, (own, other) =>
            ("/api/v1/products", new UpsertProductRequest("ISO-1", "Isolation", null, null, own.BaseUnitId, other.TaxId, 0m, [EachUnit(own)]))),
        new("ProductsController.Update", "other company's product", Put, (own, other) =>
            ($"/api/v1/products/{other.ProductId}", new UpsertProductRequest("STN-PEN", "Hijacked", null, null, own.BaseUnitId, own.TaxId, 0m, [EachUnit(own)]))),
        new("ProductsController.SetActive", "other company's product", Post, (own, other) =>
            ($"/api/v1/products/{other.ProductId}/status", new SetActiveRequest(false))),
        new("ProductsController.PreviewUnits", "other company's product", Post, (own, other) =>
            ("/api/v1/products/unit-preview", new UnitPreviewRequest(other.ProductId, own.BaseUnitId, [EachUnit(own)]))),
        new("SuppliersController.Update", "other company's supplier", Put, (own, other) =>
            ($"/api/v1/suppliers/{other.SupplierId}", new UpsertSupplierRequest("Hijacked", null, null, null, own.CurrencyId))),
        new("SuppliersController.SetActive", "other company's supplier", Post, (own, other) =>
            ($"/api/v1/suppliers/{other.SupplierId}/status", new SetActiveRequest(false))),
        new("CustomersController.Update", "other company's customer", Put, (own, other) =>
            ($"/api/v1/customers/{other.CustomerId}", new UpsertCustomerRequest("Hijacked", null, null))),
        new("CustomersController.SetActive", "other company's customer", Post, (own, other) =>
            ($"/api/v1/customers/{other.CustomerId}/status", new SetActiveRequest(false))),
        new("TaxesController.Update", "other company's tax", Put, (own, other) =>
            ($"/api/v1/taxes/{other.TaxId}", new UpsertTaxRequest("Hijacked", 1m))),
        new("TaxesController.SetActive", "other company's tax", Post, (own, other) =>
            ($"/api/v1/taxes/{other.TaxId}/status", new SetActiveRequest(false))),
        new("ExpenseCategoriesController.Rename", "other company's category", Put, (own, other) =>
            ($"/api/v1/expense-categories/{other.CategoryId}", new UpsertExpenseCategoryRequest("Hijacked"))),
        new("ExpenseCategoriesController.SetActive", "other company's category", Post, (own, other) =>
            ($"/api/v1/expense-categories/{other.CategoryId}/status", new SetActiveRequest(false))),
        new("ExpensesController.Create", "other company's category", Post, (own, other) =>
            ("/api/v1/expenses", new CreateExpenseRequest(other.CategoryId, Today, own.CurrencyId, 10m, PaymentMethod.Cash, null, null))),
        new("ExpensesController.Create", "other company's account", Post, (own, other) =>
            ("/api/v1/expenses", new CreateExpenseRequest(own.CategoryId, Today, own.CurrencyId, 10m, PaymentMethod.Cash, null, null, other.AccountId))),
        new("ExpensesController.Update", "other company's expense", Put, (own, other) =>
            ($"/api/v1/expenses/{other.ExpenseId}", new UpdateExpenseRequest(own.CategoryId, "x", "y"))),
        new("ExpensesController.Update", "own expense moved to other company's category", Put, (own, other) =>
            ($"/api/v1/expenses/{own.ExpenseId}", new UpdateExpenseRequest(other.CategoryId, "x", "y"))),
        new("ExpensesController.Cancel", "other company's expense", Post, (own, other) =>
            ($"/api/v1/expenses/{other.ExpenseId}/cancel", new CancelExpenseRequest("no"))),
        new("FinancialAccountsController.Update", "other company's account", Put, (own, other) =>
            ($"/api/v1/financial-accounts/{other.AccountId}", new UpdateFinancialAccountRequest("Hijacked", null))),
        new("FinancialAccountsController.SetActive", "other company's account", Post, (own, other) =>
            ($"/api/v1/financial-accounts/{other.AccountId}/status", new SetActiveRequest(false))),
        new("FinancialAccountsController.Transfer", "to other company's account", Post, (own, other) =>
            ("/api/v1/financial-accounts/transfers", new TransferRequest(own.AccountId, other.AccountId, 1m, Today, null))),
        new("FinancialAccountsController.Transfer", "from other company's account", Post, (own, other) =>
            ("/api/v1/financial-accounts/transfers", new TransferRequest(other.AccountId, own.AccountId, 1m, Today, null))),
        new("FinancialAccountsController.Reconcile", "other company's account", Post, (own, other) =>
            ($"/api/v1/financial-accounts/{other.AccountId}/reconcile", new ReconcileRequest([Guid.NewGuid()]))),
        new("FinancialAccountsController.Unreconcile", "other company's account", Post, (own, other) =>
            ($"/api/v1/financial-accounts/{other.AccountId}/unreconcile", new ReconcileRequest([Guid.NewGuid()]))),
        new("PurchasesController.Create", "other company's supplier", Post, (own, other) =>
            ("/api/v1/purchases", new CreatePurchaseRequest(other.SupplierId, Today, null, null, PaymentMethod.Cash, 0m, [PurchaseLine(own)]))),
        new("PurchasesController.Create", "other company's product and unit", Post, (own, other) =>
            ("/api/v1/purchases", new CreatePurchaseRequest(own.SupplierId, Today, null, null, PaymentMethod.Cash, 0m, [PurchaseLine(other)]))),
        new("PurchasesController.Create", "other company's account", Post, (own, other) =>
            ("/api/v1/purchases", new CreatePurchaseRequest(own.SupplierId, Today, null, null, PaymentMethod.Cash, 10m, [PurchaseLine(own)], other.AccountId))),
        new("PurchasesController.UpdateHeader", "other company's purchase", Put, (own, other) =>
            ($"/api/v1/purchases/{other.PurchaseId}", new UpdatePurchaseHeaderRequest("x", "y"))),
        new("PurchasesController.AddPayment", "other company's purchase", Post, (own, other) =>
            ($"/api/v1/purchases/{other.PurchaseId}/payments", new AddPurchasePaymentRequest(1m, PaymentMethod.Cash, 0m))),
        new("PurchasesController.AddRefund", "other company's purchase", Post, (own, other) =>
            ($"/api/v1/purchases/{other.PurchaseId}/refunds", new AddPurchaseRefundRequest(1m, PaymentMethod.Cash, 0m))),
        new("PurchasesController.ReversePayment", "other company's purchase", Post, (own, other) =>
            ($"/api/v1/purchases/{other.PurchaseId}/payments/{Guid.NewGuid()}/reverse", new ReversePaymentRequest("x"))),
        new("PurchasesController.Cancel", "other company's purchase", Post, (own, other) =>
            ($"/api/v1/purchases/{other.PurchaseId}/cancel", null)),
        new("PurchaseReturnsController.Create", "other company's purchase", Post, (own, other) =>
            ("/api/v1/purchase-returns", new CreatePurchaseReturnRequest(other.PurchaseId, Today, null, [new PurchaseReturnLineRequest(other.PurchaseLineId, null, 1m)]))),
        new("PurchaseReturnsController.Create", "other company's stock location (Phase 10)", Post, (own, other) =>
            ("/api/v1/purchase-returns", new CreatePurchaseReturnRequest(own.PurchaseId, Today, null, [new PurchaseReturnLineRequest(own.PurchaseLineId, null, 1m)], StockLocationId: other.LocationId))),
        new("SalesController.Create", "other company's customer", Post, (own, other) =>
            ("/api/v1/sales", new CreateSaleRequest(other.CustomerId, Today, PaymentMethod.Cash, 0m, [SaleLine(own)]))),
        new("SalesController.Create", "other company's product and unit", Post, (own, other) =>
            ("/api/v1/sales", new CreateSaleRequest(own.CustomerId, Today, PaymentMethod.Cash, 0m, [SaleLine(other)]))),
        new("SalesController.AddPayment", "other company's invoice", Post, (own, other) =>
            ($"/api/v1/sales/{other.SaleId}/payments", new AddSalePaymentRequest(1m, PaymentMethod.Cash, 0m))),
        new("SalesController.AddRefund", "other company's invoice", Post, (own, other) =>
            ($"/api/v1/sales/{other.SaleId}/refunds", new AddSaleRefundRequest(1m, PaymentMethod.Cash, 0m))),
        new("SalesController.ReversePayment", "other company's invoice", Post, (own, other) =>
            ($"/api/v1/sales/{other.SaleId}/payments/{Guid.NewGuid()}/reverse", new ReversePaymentRequest("x"))),
        new("SalesController.Cancel", "other company's invoice", Post, (own, other) =>
            ($"/api/v1/sales/{other.SaleId}/cancel", null)),
        new("SalesReturnsController.Create", "other company's invoice", Post, (own, other) =>
            ("/api/v1/sales-returns", new CreateSaleReturnRequest(other.SaleId, Today, null, [new SaleReturnLineRequest(other.SaleLineId, null, 1m)]))),
        new("StockAdjustmentsController.Create", "other company's product and unit", Post, (own, other) =>
            ("/api/v1/stock-adjustments", new CreateStockAdjustmentRequest(other.ProductId, other.ProductUnitId, AdjustmentType.Correction, AdjustmentDirection.Increase, 1m, 5m, "x"))),
        new("PricingController.SetPrice", "other company's product unit", Put, (own, other) =>
            ($"/api/v1/pricing/{other.ProductUnitId}", new SetPriceRequest(99m))),
        // Phase 6: governance writes naming the other company's member / user answer 404 (a foreign id is indistinguishable from an unknown one).
        new("GovernanceController.AddMember", "other company's user", Post, (own, other) =>
            ("/api/v1/governance/members", new AddGovernanceMemberRequest(other.UserId, GovernanceTitle.Director, null, "x"))),
        new("GovernanceController.ChangeTitle", "other company's member", Post, (own, other) =>
            ($"/api/v1/governance/members/{other.GovernanceMemberId}/title", new ChangeGovernanceTitleRequest(GovernanceTitle.Director, "x"))),
        new("GovernanceController.SetPermissions", "other company's member", Put, (own, other) =>
            ($"/api/v1/governance/members/{other.GovernanceMemberId}/permissions", new SetGovernancePermissionsRequest([], "x"))),
        new("GovernanceController.End", "other company's member", Post, (own, other) =>
            ($"/api/v1/governance/members/{other.GovernanceMemberId}/end", new EndGovernanceMemberRequest("x"))),
        new("GovernanceController.GrantAdministrator", "other company's user", Post, (own, other) =>
            ("/api/v1/governance/administrators", new GrantAdministratorRequest(other.UserId, "x"))),
        new("GovernanceController.RemoveAdministrator", "other company's user", Post, (own, other) =>
            ($"/api/v1/governance/administrators/{other.UserId}/remove", new RemoveAdministratorRequest(Guid.NewGuid(), "x"))),
        // Phase 7: role and assignment writes naming the other company's role / user / branch answer 404 or 400 (a foreign id is an unknown id).
        new("RolesController.Update", "other company's role", Put, (own, other) =>
            ($"/api/v1/roles/{other.RoleId}", new UpdateRoleRequest("Hijacked", null, []))),
        new("RolesController.Update", "other company's role (system)", Put, (own, other) =>
            ($"/api/v1/roles/{other.RoleId}", new UpdateRoleRequest("Administrator", null, ["Sales.View"], RoleScope.Company, null))),
        new("RolesController.Duplicate", "other company's role", Post, (own, other) =>
            ($"/api/v1/roles/{other.RoleId}/duplicate", null)),
        new("RolesController.SetActive", "other company's role", Post, (own, other) =>
            ($"/api/v1/roles/{other.RoleId}/status", new SetRoleActiveRequest(false))),
        new("RolesController.Create", "branch role at other company's branch", Post, (own, other) =>
            ("/api/v1/roles", new CreateRoleRequest("Cross branch", null, ["Sales.View"], RoleScope.Branch, other.BranchId))),
        new("RoleAssignmentsController.Assign", "other company's user", Post, (own, other) =>
            ($"/api/v1/users/{other.StaffUserId}/roles", new AssignRoleRequest(own.RoleId, "x"))),
        new("RoleAssignmentsController.Assign", "other company's role", Post, (own, other) =>
            ($"/api/v1/users/{own.StaffUserId}/roles", new AssignRoleRequest(other.RoleId, "x"))),
        new("RoleAssignmentsController.End", "other company's user and assignment", Post, (own, other) =>
            ($"/api/v1/users/{other.UserId}/roles/{other.AssignmentId}/end", new EndRoleAssignmentRequest("x"))),
        new("RoleAssignmentsController.End", "own user, other company's assignment", Post, (own, other) =>
            ($"/api/v1/users/{own.StaffUserId}/roles/{other.AssignmentId}/end", new EndRoleAssignmentRequest("x"))),
        new("ExchangeRatesController.Update", "other company's rate", Put, (own, other) =>
            ($"/api/v1/exchange-rates/{other.ExchangeRateId}", new UpsertExchangeRateRequest(own.CurrencyId, 5m, Today))),
    ];

    /// <summary>Actions with no company data: platform masters (units, currencies, permissions, roles), authentication, and the caller's own account.</summary>
    public static readonly HashSet<string> PlatformActions =
    [
        "UnitsController.List", "UnitsController.Get", "UnitsController.Create", "UnitsController.Update", "UnitsController.SetActive",
        "CurrenciesController.List", "CurrenciesController.Create", "CurrenciesController.Update", "CurrenciesController.SetActive",
        "PermissionsController.List",
        // Phase 7: units and currencies stay platform rows; their company writes are refused (403 reference_data_platform_managed) and the platform surface manages them.
        "PlatformReferenceDataController.Units", "PlatformReferenceDataController.CreateUnit", "PlatformReferenceDataController.UpdateUnit", "PlatformReferenceDataController.SetUnitActive",
        "PlatformReferenceDataController.Currencies", "PlatformReferenceDataController.CreateCurrency", "PlatformReferenceDataController.UpdateCurrency", "PlatformReferenceDataController.SetCurrencyActive",
        "UsersController.UpdateOwnUsername", "UsersController.ChangeOwnPassword",
        // Phase 6: the platform surface never reads company data (no company context: company-owned queries fail closed); the governance catalogue is global.
        "PlatformAuthController.Login", "PlatformCompaniesController.List", "PlatformCompaniesController.Currencies", "PlatformCompaniesController.Create",
        "PlatformCompaniesController.SetStatus", "GovernanceController.Permissions",
        "AuthController.Login", "AuthController.Refresh", "AuthController.SwitchCompany", "AuthController.Logout", "AuthController.Me",
    ];

    /// <summary>Company-scoped actions exercised by other tests: users (AuthCompanyApiTests, L1), settings update and the create-only endpoints
    /// whose isolation is the stamping/uniqueness proven by <c>CompanyIsolationApiTests</c> (create under A leaves B unchanged).</summary>
    public static readonly HashSet<string> CoveredElsewhere =
    [
        "UsersController.List", "UsersController.Get", "UsersController.Create", "UsersController.Update", "UsersController.SetStatus", "UsersController.ResetPassword",
        "SettingsController.Update", "BranchesController.Create", "SuppliersController.Create", "CustomersController.Create", "TaxesController.Create",
        "ExpenseCategoriesController.Create", "FinancialAccountsController.Create", "ExchangeRatesController.Create",
        // Phase 5 access API: a user (or assignment) of another company answers 404 - UserAccessApiTests.Get_UnknownUserAndNonMemberAndForeignCompanyUser_AreNotFound_WithIdenticalBodies
        // (read, events and a write against a foreign-company user); assignments carry their own company id and composite FKs (AddUserBranchAssignmentsMigrationTests constraint test).
        "UserAccessController.Get", "UserAccessController.Events", "UserAccessController.SetScope", "UserAccessController.AddAssignment",
        "UserAccessController.EndAssignment", "UserAccessController.SetPrimary", "UserAccessController.Reassign", "UserAccessController.SetStatus",
        // Phase 9: stock transfers - StockTransferApiTests.OtherCompanysIds_Are404OrRefused_AndNothingChanges (list / detail of the other company, every write on its
        // transfer is a 404, create naming its locations / products / units is a 400 on the field, row counts unchanged) on the same two-company IsolationWorld.
        "StockTransfersController.List", "StockTransfersController.Get", "StockTransfersController.Create", "StockTransfersController.Update", "StockTransfersController.Submit",
        "StockTransfersController.Approve", "StockTransfersController.Reject", "StockTransfersController.Cancel", "StockTransfersController.Dispatch",
        "StockTransfersController.Receive", "StockTransfersController.Resolve",
    ];
}
