namespace Sellora.Domain.Permissions;

/// <summary>
/// The full Phase 1 permission catalogue (docx §6, §8.4). This is system-seeded and read-only —
/// administrators map these to roles, they never define new permission codes. Codes for modules not
/// yet built (Inventory, Purchase, Sales, Reports, Masters) are seeded now so the Roles UI can present
/// the complete matrix; the corresponding screens/endpoints arrive in later phases and simply start
/// checking these already-existing codes.
/// </summary>
public static class PermissionCatalog
{
    /// <summary>
    /// Codes a branch-scoped role may hold (Phase 7, D5, owner Q2; Phase 8 O3 adds Inventory.View, Reports.Stock, Reports.Movement; Inventory.Create/Edit/Price and financial reports stay company-only).
    /// Single source of the scope metadata: <see cref="PermissionDefinition.Scope"/> of <see cref="All"/> is derived from it.
    /// </summary>
    private static readonly HashSet<string> BranchEligibleCodes = new(StringComparer.Ordinal)
    {
        "Sales.View", "Sales.Create", "Sales.PriceOverride", "Sales.BillDiscount", "Sales.Edit", "Sales.Receive", "Sales.Return", "Sales.Refund", "Sales.Cancel",
        "Purchase.View", "Purchase.Create", "Purchase.Edit", "Purchase.Pay", "Purchase.Return", "Purchase.Refund", "Purchase.Cancel",
        "Inventory.Adjust", "StockLocations.View", "Inventory.View",
        "StockTransfers.View", "StockTransfers.Create", "StockTransfers.Approve", "StockTransfers.Dispatch", "StockTransfers.Receive", "StockTransfers.ApproveReceipt", "StockTransfers.Cancel",
        "Shipments.View", "Shipments.Plan", "Shipments.Load", // FI-D31 (Reservations.Release stays company-only)
        "Expenses.View", "Expenses.Create", "Expenses.Edit", "Expenses.Cancel",
        "Customers.View", // Phase 11 D15: branch cashiers pick named customers at POS (customers stay company master data)
        "Reports.Sales", "Reports.Purchase", "Reports.Stock", "Reports.Movement", "Reports.Tax",
        BranchRolesView, BranchRolesManage, BranchUsersAssignRoles,
        BranchUsersView, BranchUsersCreate, BranchUsersEdit, BranchUsersDisable, BranchUsersResetPassword, // Phase 01b, FI-D44
    };

    public const string CompanyUsersAssignRoles = "Company.Users.AssignRoles";
    public const string BranchRolesView = "Branch.Roles.View";
    public const string BranchRolesManage = "Branch.Roles.Manage";
    public const string BranchUsersAssignRoles = "Branch.Users.AssignRoles";
    public const string BranchUsersView = "Branch.Users.View";
    public const string BranchUsersCreate = "Branch.Users.Create";
    public const string BranchUsersEdit = "Branch.Users.Edit";
    public const string BranchUsersDisable = "Branch.Users.Disable";
    public const string BranchUsersResetPassword = "Branch.Users.ResetPassword";

    /// <summary>
    /// Branch administration codes (FI-D45): only a company administrator (company path) may put them into a role, assign or end such a role,
    /// or edit, duplicate or deactivate a role holding them. Their holders are company administration for the FI-D14 target rule.
    /// </summary>
    public static readonly IReadOnlySet<string> BranchAdministrationCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        BranchRolesView, BranchRolesManage, BranchUsersAssignRoles,
        BranchUsersView, BranchUsersCreate, BranchUsersEdit, BranchUsersDisable, BranchUsersResetPassword,
    };

    /// <summary>True when <paramref name="code"/> is a branch administration code (FI-D45).</summary>
    public static bool IsBranchAdministration(string code) => BranchAdministrationCodes.Contains(code);

    /// <summary>True when a branch-scoped role may hold <paramref name="code"/>.</summary>
    public static bool IsBranchEligible(string code) => BranchEligibleCodes.Contains(code);

    /// <summary>Scope of a catalogue code; unknown codes are CompanyOnly (fail closed).</summary>
    public static PermissionScope ScopeOf(string code) => IsBranchEligible(code) ? PermissionScope.CompanyOrBranch : PermissionScope.CompanyOnly;

    private static readonly IReadOnlyList<PermissionDefinition> Definitions = new List<PermissionDefinition>
    {
        // Administration — Users
        new("Users.View", "Administration", "View", "View the user list and user details."),
        new("Users.Create", "Administration", "Create", "Invite/create new users."),
        new("Users.Edit", "Administration", "Edit", "Edit user details and role assignment."),
        new("Users.Disable", "Administration", "Disable", "Activate or deactivate a user account."),
        new("Users.ResetPassword", "Administration", "ResetPassword", "Trigger a password reset for a user."),

        // Administration — Roles & Permissions
        new("Roles.View", "Administration", "View", "View roles and their permission mappings."),
        new("Roles.Create", "Administration", "Create", "Create new roles."),
        new("Roles.Edit", "Administration", "Edit", "Edit a role's details and permission mapping."),
        new("Roles.Duplicate", "Administration", "Duplicate", "Duplicate an existing role."),
        new("Roles.Disable", "Administration", "Disable", "Activate or deactivate a role."),

        // Administration — Scoped role assignment and branch role administration (Phase 7)
        new(CompanyUsersAssignRoles, "Administration", "AssignRoles", "Assign and end company-wide roles for users (company-wide administrators only)."),
        new(BranchRolesView, "Administration", "View", "View the roles scoped to the branch."),
        new(BranchRolesManage, "Administration", "Manage", "Create, edit, duplicate and deactivate roles scoped to the branch."),
        new(BranchUsersAssignRoles, "Administration", "AssignRoles", "Assign and end branch roles for users allocated to the branch."),

        // Administration — Branch user administration (Phase 01b, FI-D44): users wholly inside the branch, never company administration
        new(BranchUsersView, "Administration", "View", "View the users allocated to the branch."),
        new(BranchUsersCreate, "Administration", "Create", "Create users who are allocated to the active branch with one of its roles."),
        new(BranchUsersEdit, "Administration", "Edit", "Edit the details of users allocated to the branch."),
        new(BranchUsersDisable, "Administration", "Disable", "Activate or deactivate users allocated to the branch."),
        new(BranchUsersResetPassword, "Administration", "ResetPassword", "Reset the password of users allocated to the branch."),

        // Administration — Permissions catalogue (read-only)
        new("Permissions.View", "Administration", "View", "View the read-only system permission catalogue."),

        // Administration — Branches (Phase 2; Administrator only, granted via PermissionResolver)
        new("Company.Branches.View", "Administration", "View", "View the company's branches."),
        new("Company.Branches.Create", "Administration", "Create", "Add new branches."),
        new("Company.Branches.Edit", "Administration", "Edit", "Edit branch details, GSTIN and invoice prefix."),
        new("Company.Branches.Disable", "Administration", "Disable", "Activate or deactivate a branch."),
        new("Company.Users.ManageBranchAccess", "Administration", "ManageBranchAccess", "Manage a user's company membership and branch allocations (company-wide administrators only)."),

        // Administration — Warehouses and vehicles (Phase 4; Administrator only, granted via PermissionResolver)
        new("Company.Warehouses.View", "Administration", "View", "View the company's warehouses."),
        new("Company.Warehouses.Create", "Administration", "Create", "Add company or branch warehouses."),
        new("Company.Warehouses.Edit", "Administration", "Edit", "Rename warehouses."),
        new("Company.Warehouses.Disable", "Administration", "Disable", "Activate or deactivate a warehouse."),
        new("Company.Vehicles.View", "Administration", "View", "View the company's vehicles."),
        new("Company.Vehicles.Create", "Administration", "Create", "Add vehicles."),
        new("Company.Vehicles.Edit", "Administration", "Edit", "Edit vehicle details and branch assignment."),
        new("Company.Vehicles.Disable", "Administration", "Disable", "Activate or deactivate a vehicle."),

        // Administration — Basic Masters / Settings
        new("Masters.View", "Administration", "View", "View units, taxes, currencies, exchange rates and business settings."),
        new("Masters.Edit", "Administration", "Edit", "Edit units, taxes, currencies, exchange rates and business settings."),

        // Inventory
        new("Inventory.View", "Inventory", "View", "View stock overview, details and movement."),
        new("Inventory.Create", "Inventory", "Create", "Create new products."),
        new("Inventory.Edit", "Inventory", "Edit", "Edit existing products."),
        new("Inventory.Delete", "Inventory", "Delete", "Delete products that have never been used in a transaction."),
        new("Inventory.Adjust", "Inventory", "Adjust", "Post stock adjustments (damage, loss, correction, etc.)."),
        new("Inventory.Price", "Inventory", "Price", "Configure product units and prices."),
        new("Inventory.Categories", "Inventory", "Manage categories", "Create, edit, activate and deactivate product categories and subcategories."),
        new("Tax.View", "Inventory", "View tax setup", "View tax components, tax profiles and the tax mapping of products and categories."),
        new("Tax.Manage", "Inventory", "Manage tax setup", "Create and edit tax components, tax profiles, their dated versions and the tax assignments."),
        new("StockLocations.View", "Inventory", "View locations", "View stock locations (branch stores, warehouses, vehicles, in-transit)."),
        new("StockTransfers.View", "Inventory", "View transfers", "View stock transfers."),
        new("StockTransfers.Create", "Inventory", "Create transfers", "Create, edit and submit stock transfer requests."),
        new("StockTransfers.Approve", "Inventory", "Approve transfers", "Approve or reject stock transfer requests."),
        new("StockTransfers.Dispatch", "Inventory", "Dispatch transfers", "Dispatch approved stock transfers from the source location."),
        new("StockTransfers.Receive", "Inventory", "Receive transfers", "Receive dispatched stock transfers at the destination location."),
        new("StockTransfers.ApproveReceipt", "Inventory", "Approve receipts", "Approve or reject recorded consignment receipts at the destination when the company requires supervisor approval."),
        new("StockTransfers.Cancel", "Inventory", "Cancel transfers", "Cancel stock transfers before they are dispatched."),
        new("StockTransfers.Resolve", "Inventory", "Resolve transfers", "Resolve in-transit shortages by writing them off or returning them to the source (company-wide only)."),
        new("Shipments.View", "Inventory", "View shipments", "View shipments, consignments and their allocations."),
        new("Shipments.Plan", "Inventory", "Plan shipments", "Create, edit, allocate stock to, assign vehicles to and cancel shipments and consignments."),
        new("Shipments.Load", "Inventory", "Load shipments", "Record loading of consignments and mark shipments ready to dispatch."),
        new("Reservations.Release", "Inventory", "Release reservations", "Release stock reservations that are no longer needed (company-wide only)."),

        // Purchase — Suppliers
        new("Suppliers.View", "Purchase", "View", "View the supplier list."),
        new("Suppliers.Create", "Purchase", "Create", "Add new suppliers."),
        new("Suppliers.Edit", "Purchase", "Edit", "Edit supplier details."),
        new("Suppliers.Disable", "Purchase", "Disable", "Activate or deactivate a supplier."),

        // Purchase
        new("Purchase.View", "Purchase", "View", "View purchase orders."),
        new("Purchase.Create", "Purchase", "Create", "Create new purchase orders."),
        new("Purchase.Edit", "Purchase", "Edit", "Edit draft purchase orders."),
        new("Purchase.Pay", "Purchase", "Pay", "Record payments against purchase orders."),
        new("Purchase.Return", "Purchase", "Return", "Return goods to a supplier."),
        new("Purchase.Refund", "Purchase", "Refund", "Record supplier refunds and reverse purchase payments."),
        new("Purchase.Cancel", "Purchase", "Cancel", "Cancel a completed purchase order."),

        // Sales & Billing — Customers
        new("Customers.View", "Sales", "View", "View the customer list."),
        new("Customers.Create", "Sales", "Create", "Add new customers."),
        new("Customers.Edit", "Sales", "Edit", "Edit customer details."),
        new("Customers.Disable", "Sales", "Disable", "Activate or deactivate a customer."),

        // Sales & Billing
        new("Sales.View", "Sales", "View", "View sales invoices."),
        new("Sales.Create", "Sales", "Create", "Create sales through POS billing."),
        new("Sales.PriceOverride", "Sales", "PriceOverride", "Charge a price other than the list price, or give a discount, at the POS."),
        new("Sales.BillDiscount", "Sales", "BillDiscount", "Give a whole-bill discount on a sale (capped by the company's maximum bill discount)."),
        new("Sales.Edit", "Sales", "Edit", "Edit an in-progress sale."),
        new("Sales.Receive", "Sales", "Receive", "Record payments received against sales invoices."),
        new("Sales.Return", "Sales", "Return", "Take back goods from a customer."),
        new("Sales.Refund", "Sales", "Refund", "Refund customers and reverse sales payments."),
        new("Sales.Cancel", "Sales", "Cancel", "Cancel a completed sales invoice."),

        // Accounts — Expenses
        new("Expenses.View", "Accounts", "View", "View operating expenses."),
        new("Expenses.Create", "Accounts", "Create", "Record operating expenses."),
        new("Expenses.Edit", "Accounts", "Edit", "Edit an expense's category, reference and notes."),
        new("Expenses.Cancel", "Accounts", "Cancel", "Cancel an expense."),
        new("Expenses.Categories", "Accounts", "Categories", "Create, rename and deactivate expense categories."),

        // Accounts — Financial Transactions
        new("FinancialTransactions.View", "Accounts", "View", "View the financial transaction history."),

        // Accounts — Cash & Bank
        new("CashBank.View", "Accounts", "View", "View cash and bank accounts, balances and account history."),
        new("CashBank.Manage", "Accounts", "Manage", "Create, edit and deactivate cash and bank accounts."),
        new("CashBank.Transfer", "Accounts", "Transfer", "Transfer money between accounts, including cash deposits and withdrawals."),
        new("CashBank.Reconcile", "Accounts", "Reconcile", "Reconcile account transactions against statements."),
        new("CashBank.Fund", "Accounts", "Fund", "Record funding (capital, loan, deposit, customer receipt) into cash and bank accounts."),
        new("CashBank.Reverse", "Accounts", "Reverse", "Reverse funding, transfers and opening balances posted in error."),

        // Reports
        new("Reports.Stock", "Reports", "View", "View the current stock report."),
        new("Reports.Movement", "Reports", "View", "View the stock movement report."),
        new("Reports.Purchase", "Reports", "View", "View the purchase report."),
        new("Reports.Sales", "Reports", "View", "View the sales report."),
        new("Reports.Tax", "Reports", "View", "View the tax reports: GST or VAT summary and the HSN summary."),
        new("Reports.Accounts", "Reports", "View", "View the Accounts reports: payments, outstanding balances, expenses, cash flow, cash & bank and payment methods."),
        new("Reports.Profit", "Reports", "View", "View the profit/margin summary report."),
    };

    // Declared after Definitions: static initialisers run in textual order.
    public static readonly IReadOnlyList<PermissionDefinition> All = Definitions
        .Select(d => d with { Scope = ScopeOf(d.Code) })
        .ToList();
}
