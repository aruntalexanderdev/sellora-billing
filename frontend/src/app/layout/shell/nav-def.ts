export interface NavItemDef {
  label: string;
  routerLink: string;
  /** Permission code (or any-of list) at the working branch; '' = always visible. */
  permission: string | string[];
  /** Governance-gated entry: visible only to an active governance member holding this governance permission code. */
  governance?: string;
}

export interface NavGroupDef {
  label: string;
  items: NavItemDef[];
}

/**
 * The whole sidebar. Phase 11 packages add no entries here: every screen's nav entry already exists and points at a
 * route that is (or will be) registered in features/<area>/<area>.routes.ts.
 */
export const NAV_DEF: NavGroupDef[] = [
  { label: 'Overview', items: [{ label: 'Dashboard', routerLink: '/dashboard', permission: '' }] },
  {
    label: 'Company',
    items: [
      { label: 'Branches', routerLink: '/company/branches', permission: 'Company.Branches.View' },
      { label: 'Warehouses', routerLink: '/company/warehouses', permission: 'Company.Warehouses.View' },
      { label: 'Vehicles', routerLink: '/company/vehicles', permission: 'Company.Vehicles.View' },
      { label: 'Stock Locations', routerLink: '/company/stock-locations', permission: 'StockLocations.View' },
      { label: 'Governance', routerLink: '/company/governance', permission: '', governance: 'Company.Governance.View' },
    ],
  },
  {
    label: 'Administration',
    items: [
      // User access (branch allocations, roles) is a tab inside a user: /administration/users/:id/access.
      { label: 'Users', routerLink: '/administration/users', permission: ['Users.View', 'Branch.Users.View'] },
      { label: 'Roles & Permissions', routerLink: '/administration/roles', permission: ['Roles.View', 'Branch.Roles.View'] },
      { label: 'Permissions', routerLink: '/administration/permissions', permission: 'Permissions.View' },
      { label: 'Units', routerLink: '/administration/units', permission: 'Masters.View' },
      { label: 'Currencies', routerLink: '/administration/currencies', permission: 'Masters.View' },
      { label: 'Countries', routerLink: '/administration/countries', permission: 'Masters.View' },
      { label: 'States', routerLink: '/administration/states', permission: 'Masters.View' },
      { label: 'Exchange Rates', routerLink: '/administration/exchange-rates', permission: 'Masters.View' },
      { label: 'Business Settings', routerLink: '/administration/settings', permission: 'Masters.View' },
    ],
  },
  {
    label: 'Inventory',
    items: [
      { label: 'Stock Overview', routerLink: '/inventory/stock', permission: 'Inventory.View' },
      { label: 'Products', routerLink: '/inventory/products', permission: 'Inventory.View' },
      { label: 'Categories', routerLink: '/inventory/categories', permission: ['Inventory.View', 'Inventory.Categories'] },
      { label: 'Tax setup', routerLink: '/inventory/tax-setup', permission: 'Tax.View' },
      { label: 'Stock Adjustments', routerLink: '/inventory/stock-adjustments', permission: 'Inventory.View' },
      { label: 'Stock Transfers', routerLink: '/inventory/stock-transfers', permission: 'StockTransfers.View' },
      { label: 'Shipments', routerLink: '/inventory/shipments', permission: 'Shipments.View' },
      { label: 'Consignments', routerLink: '/inventory/consignments', permission: ['Shipments.View', 'StockTransfers.Receive', 'StockTransfers.View'] },
      { label: 'Transit', routerLink: '/inventory/transit', permission: ['Shipments.View', 'StockTransfers.Receive', 'StockTransfers.View'] },
      { label: 'Reservations', routerLink: '/inventory/stock-reservations', permission: 'Shipments.View' },
      { label: 'Stock & Pricing', routerLink: '/inventory/pricing', permission: 'Inventory.Price' },
    ],
  },
  {
    label: 'Purchases',
    items: [
      { label: 'Purchases', routerLink: '/purchases', permission: 'Purchase.View' },
      { label: 'Purchase Returns', routerLink: '/purchases/returns', permission: 'Purchase.View' },
      { label: 'Suppliers', routerLink: '/purchases/suppliers', permission: 'Suppliers.View' },
    ],
  },
  {
    label: 'Sales & Billing',
    items: [
      { label: 'New Sale', routerLink: '/sales/new', permission: 'Sales.Create' },
      { label: 'Van Workspace', routerLink: '/sales/van', permission: 'Sales.Create' },
      { label: 'Sales', routerLink: '/sales', permission: 'Sales.View' },
      { label: 'Sales Returns', routerLink: '/sales/returns', permission: 'Sales.View' },
      { label: 'Customers', routerLink: '/sales/customers', permission: 'Customers.View' },
    ],
  },
  {
    label: 'Accounts',
    items: [
      { label: 'Expenses', routerLink: '/accounts/expenses', permission: 'Expenses.View' },
      { label: 'Cash & Bank', routerLink: '/accounts/cash-bank', permission: 'CashBank.View' },
      { label: 'Financial Transactions', routerLink: '/accounts/financial-transactions', permission: 'FinancialTransactions.View' },
    ],
  },
  {
    label: 'Accounts Reports',
    items: [
      { label: 'Purchase Payments', routerLink: '/accounts/reports/purchase-payments', permission: 'Reports.Accounts' },
      { label: 'Sales Payments', routerLink: '/accounts/reports/sales-payments', permission: 'Reports.Accounts' },
      { label: 'Supplier Outstanding', routerLink: '/accounts/reports/supplier-outstanding', permission: 'Reports.Accounts' },
      { label: 'Customer Outstanding', routerLink: '/accounts/reports/customer-outstanding', permission: 'Reports.Accounts' },
      { label: 'Expense Report', routerLink: '/accounts/reports/expenses', permission: 'Reports.Accounts' },
      { label: 'Cash Flow', routerLink: '/accounts/reports/cash-flow', permission: 'Reports.Accounts' },
      { label: 'Cash & Bank Report', routerLink: '/accounts/reports/cash-bank', permission: 'Reports.Accounts' },
      { label: 'Payment Methods', routerLink: '/accounts/reports/payment-methods', permission: 'Reports.Accounts' },
    ],
  },
  {
    label: 'Reports',
    items: [
      { label: 'Current Stock', routerLink: '/reports/stock', permission: 'Reports.Stock' },
      { label: 'Stock Movement', routerLink: '/reports/stock-movement', permission: 'Reports.Movement' },
      { label: 'Purchase Report', routerLink: '/reports/purchases', permission: 'Reports.Purchase' },
      { label: 'Sales Report', routerLink: '/reports/sales', permission: 'Reports.Sales' },
      { label: 'Profit / Margin', routerLink: '/reports/profit', permission: 'Reports.Profit' },
      { label: 'GST Summary', routerLink: '/reports/tax/gst-summary', permission: 'Reports.Tax' },
      { label: 'HSN Summary', routerLink: '/reports/tax/hsn-summary', permission: 'Reports.Tax' },
      { label: 'VAT Summary', routerLink: '/reports/tax/vat-summary', permission: 'Reports.Tax' },
    ],
  },
  { label: 'System', items: [{ label: 'UI Kit', routerLink: '/ui-showcase', permission: '' }] },
];

export interface NavGates {
  hasPermission(code: string): boolean;
  hasGovernance(code: string): boolean;
}

/** Pure filter: drops items the caller may not see, then empty groups. */
export function filterNav(def: NavGroupDef[], gates: NavGates): { label: string; items: { label: string; routerLink: string }[] }[] {
  const allowed = (it: NavItemDef): boolean => {
    if (it.governance) return gates.hasGovernance(it.governance);
    const codes = Array.isArray(it.permission) ? it.permission : it.permission ? [it.permission] : [];
    return codes.length === 0 || codes.some((c) => gates.hasPermission(c));
  };
  return def
    .map((g) => ({ label: g.label, items: g.items.filter(allowed).map(({ label, routerLink }) => ({ label, routerLink })) }))
    .filter((g) => g.items.length > 0);
}
