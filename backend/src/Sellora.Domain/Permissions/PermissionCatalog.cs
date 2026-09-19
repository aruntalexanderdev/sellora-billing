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
    public static readonly IReadOnlyList<PermissionDefinition> All = new List<PermissionDefinition>
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

        // Administration — Permissions catalogue (read-only)
        new("Permissions.View", "Administration", "View", "View the read-only system permission catalogue."),

        // Administration — Basic Masters / Settings
        new("Masters.View", "Administration", "View", "View units, taxes, currencies, exchange rates and business settings."),
        new("Masters.Edit", "Administration", "Edit", "Edit units, taxes, currencies, exchange rates and business settings."),

        // Inventory
        new("Inventory.View", "Inventory", "View", "View stock overview, details and movement."),
        new("Inventory.Create", "Inventory", "Create", "Create new products."),
        new("Inventory.Edit", "Inventory", "Edit", "Edit existing products."),
        new("Inventory.Adjust", "Inventory", "Adjust", "Post stock adjustments (damage, loss, correction, etc.)."),
        new("Inventory.Price", "Inventory", "Price", "Configure selling modes and prices."),

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
        new("Purchase.Cancel", "Purchase", "Cancel", "Cancel a completed purchase order."),

        // Sales & Billing — Customers
        new("Customers.View", "Sales", "View", "View the customer list."),
        new("Customers.Create", "Sales", "Create", "Add new customers."),
        new("Customers.Edit", "Sales", "Edit", "Edit customer details."),
        new("Customers.Disable", "Sales", "Disable", "Activate or deactivate a customer."),

        // Sales & Billing
        new("Sales.View", "Sales", "View", "View sales invoices."),
        new("Sales.Create", "Sales", "Create", "Create sales through POS billing."),
        new("Sales.Edit", "Sales", "Edit", "Edit an in-progress sale."),
        new("Sales.Receive", "Sales", "Receive", "Record payments received against sales invoices."),
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

        // Reports
        new("Reports.Stock", "Reports", "View", "View the current stock report."),
        new("Reports.Movement", "Reports", "View", "View the stock movement report."),
        new("Reports.Purchase", "Reports", "View", "View the purchase report."),
        new("Reports.Sales", "Reports", "View", "View the sales report."),
        new("Reports.Profit", "Reports", "View", "View the profit/margin summary report."),
    };
}
