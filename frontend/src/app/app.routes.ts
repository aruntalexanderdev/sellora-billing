import { Routes } from '@angular/router';
import { Shell } from './layout/shell/shell';
import { UiShowcase } from './features/ui-showcase/ui-showcase';
import { Login } from './features/auth/login/login';
import { ForgotPassword } from './features/auth/forgot-password/forgot-password';
import { PermissionDenied } from './features/permission-denied/permission-denied';
import { ProfilePage } from './features/profile/profile-page/profile-page';
import { UserList } from './features/administration/users/user-list/user-list';
import { UserForm } from './features/administration/users/user-form/user-form';
import { RoleList } from './features/administration/roles/role-list/role-list';
import { RoleForm } from './features/administration/roles/role-form/role-form';
import { PermissionList } from './features/administration/permissions/permission-list/permission-list';
import { UnitList } from './features/masters/units/unit-list/unit-list';
import { UnitForm } from './features/masters/units/unit-form/unit-form';
import { TaxList } from './features/masters/taxes/tax-list/tax-list';
import { TaxForm } from './features/masters/taxes/tax-form/tax-form';
import { SettingsPage } from './features/masters/settings/settings-page/settings-page';
import { CurrencyList } from './features/administration/currencies/currency-list/currency-list';
import { CurrencyForm } from './features/administration/currencies/currency-form/currency-form';
import { ExchangeRateList } from './features/administration/exchange-rates/exchange-rate-list/exchange-rate-list';
import { ExchangeRateForm } from './features/administration/exchange-rates/exchange-rate-form/exchange-rate-form';
import { SupplierList } from './features/purchases/suppliers/supplier-list/supplier-list';
import { SupplierForm } from './features/purchases/suppliers/supplier-form/supplier-form';
import { ProductList } from './features/inventory/products/product-list/product-list';
import { ProductForm } from './features/inventory/products/product-form/product-form';
import { PurchaseList } from './features/purchases/purchase-list/purchase-list';
import { PurchaseForm } from './features/purchases/purchase-form/purchase-form';
import { StockOverview } from './features/inventory/stock/stock-overview/stock-overview';
import { StockDetail } from './features/inventory/stock/stock-detail/stock-detail';
import { StockAdjustmentForm } from './features/inventory/stock-adjustments/stock-adjustment-form/stock-adjustment-form';
import { PricingPage } from './features/inventory/pricing/pricing-page/pricing-page';
import { CustomerList } from './features/sales/customers/customer-list/customer-list';
import { CustomerForm } from './features/sales/customers/customer-form/customer-form';
import { Pos } from './features/sales/pos/pos';
import { SalesList } from './features/sales/sales-list/sales-list';
import { CurrentStockReport } from './features/reports/current-stock-report/current-stock-report';
import { StockMovementReport } from './features/reports/stock-movement-report/stock-movement-report';
import { PurchaseReport } from './features/reports/purchase-report/purchase-report';
import { SalesReport } from './features/reports/sales-report/sales-report';
import { ProfitSummaryReport } from './features/reports/profit-summary-report/profit-summary-report';
import { DashboardPage } from './features/dashboard/dashboard-page/dashboard-page';
import { authGuard, permissionGuard } from './core/auth/auth.guard';
import { unsavedChangesGuard } from './core/unsaved-changes.guard';

export const routes: Routes = [
  { path: 'login', component: Login },
  { path: 'forgot-password', component: ForgotPassword },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', component: DashboardPage, data: { group: 'Dashboard', page: 'Dashboard' } },
      { path: 'ui-showcase', component: UiShowcase, data: { group: 'System', page: 'UI Kit' } },
      { path: 'profile', component: ProfilePage, data: { group: 'Account', page: 'My Profile' } },
      { path: 'permission-denied', component: PermissionDenied, data: { group: 'System', page: 'Permission denied' } },

      {
        path: 'administration/users',
        component: UserList,
        canActivate: [permissionGuard('Users.View')],
        data: { group: 'Administration', page: 'Users' },
      },
      {
        path: 'administration/users/new',
        component: UserForm,
        canActivate: [permissionGuard('Users.Create')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'Invite user' },
      },
      {
        path: 'administration/users/:id/edit',
        component: UserForm,
        canActivate: [permissionGuard('Users.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'Edit user' },
      },

      {
        path: 'administration/roles',
        component: RoleList,
        canActivate: [permissionGuard('Roles.View')],
        data: { group: 'Administration', page: 'Roles & Permissions' },
      },
      {
        path: 'administration/roles/new',
        component: RoleForm,
        canActivate: [permissionGuard('Roles.Create')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'New role' },
      },
      {
        path: 'administration/roles/:id/edit',
        component: RoleForm,
        canActivate: [permissionGuard('Roles.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'Edit role' },
      },

      {
        path: 'administration/permissions',
        component: PermissionList,
        canActivate: [permissionGuard('Permissions.View')],
        data: { group: 'Administration', page: 'Permissions' },
      },

      {
        path: 'administration/units',
        component: UnitList,
        canActivate: [permissionGuard('Masters.View')],
        data: { group: 'Administration', page: 'Units' },
      },
      {
        path: 'administration/units/new',
        component: UnitForm,
        canActivate: [permissionGuard('Masters.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'New unit' },
      },
      {
        path: 'administration/units/:id/edit',
        component: UnitForm,
        canActivate: [permissionGuard('Masters.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'Edit unit' },
      },

      {
        path: 'administration/taxes',
        component: TaxList,
        canActivate: [permissionGuard('Masters.View')],
        data: { group: 'Administration', page: 'Taxes' },
      },
      {
        path: 'administration/taxes/new',
        component: TaxForm,
        canActivate: [permissionGuard('Masters.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'New tax' },
      },
      {
        path: 'administration/taxes/:id/edit',
        component: TaxForm,
        canActivate: [permissionGuard('Masters.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'Edit tax' },
      },

      {
        path: 'administration/currencies',
        component: CurrencyList,
        canActivate: [permissionGuard('Masters.View')],
        data: { group: 'Administration', page: 'Currencies' },
      },
      {
        path: 'administration/currencies/new',
        component: CurrencyForm,
        canActivate: [permissionGuard('Masters.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'New currency' },
      },
      {
        path: 'administration/currencies/:id/edit',
        component: CurrencyForm,
        canActivate: [permissionGuard('Masters.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'Edit currency' },
      },

      {
        path: 'administration/exchange-rates',
        component: ExchangeRateList,
        canActivate: [permissionGuard('Masters.View')],
        data: { group: 'Administration', page: 'Exchange rates' },
      },
      {
        path: 'administration/exchange-rates/new',
        component: ExchangeRateForm,
        canActivate: [permissionGuard('Masters.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'New exchange rate' },
      },
      {
        path: 'administration/exchange-rates/:id/edit',
        component: ExchangeRateForm,
        canActivate: [permissionGuard('Masters.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'Edit exchange rate' },
      },

      {
        path: 'administration/settings',
        component: SettingsPage,
        canActivate: [permissionGuard('Masters.View')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Administration', page: 'Business Settings' },
      },

      {
        path: 'purchases/suppliers',
        component: SupplierList,
        canActivate: [permissionGuard('Suppliers.View')],
        data: { group: 'Purchases', page: 'Suppliers' },
      },
      {
        path: 'purchases/suppliers/new',
        component: SupplierForm,
        canActivate: [permissionGuard('Suppliers.Create')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Purchases', page: 'New supplier' },
      },
      {
        path: 'purchases/suppliers/:id/edit',
        component: SupplierForm,
        canActivate: [permissionGuard('Suppliers.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Purchases', page: 'Edit supplier' },
      },

      {
        path: 'inventory/products',
        component: ProductList,
        canActivate: [permissionGuard('Inventory.View')],
        data: { group: 'Inventory', page: 'Products' },
      },
      {
        path: 'inventory/products/new',
        component: ProductForm,
        canActivate: [permissionGuard('Inventory.Create')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Inventory', page: 'New product' },
      },
      {
        path: 'inventory/products/:id/edit',
        component: ProductForm,
        canActivate: [permissionGuard('Inventory.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Inventory', page: 'Edit product' },
      },

      {
        path: 'inventory/stock',
        component: StockOverview,
        canActivate: [permissionGuard('Inventory.View')],
        data: { group: 'Inventory', page: 'Stock Overview' },
      },
      {
        path: 'inventory/stock-adjustments/new',
        component: StockAdjustmentForm,
        canActivate: [permissionGuard('Inventory.Adjust')],
        data: { group: 'Inventory', page: 'Adjust Stock' },
      },
      {
        path: 'inventory/stock/:id',
        component: StockDetail,
        canActivate: [permissionGuard('Inventory.View')],
        data: { group: 'Inventory', page: 'Stock Details' },
      },

      {
        path: 'inventory/pricing',
        component: PricingPage,
        canActivate: [permissionGuard('Inventory.Price')],
        data: { group: 'Inventory', page: 'Stock & Pricing' },
      },

      {
        path: 'purchases',
        component: PurchaseList,
        canActivate: [permissionGuard('Purchase.View')],
        data: { group: 'Purchases', page: 'Purchases' },
      },
      {
        path: 'purchases/new',
        component: PurchaseForm,
        canActivate: [permissionGuard('Purchase.Create')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Purchases', page: 'New purchase' },
      },
      {
        path: 'purchases/returns',
        loadComponent: () => import('./features/purchases/purchase-returns/purchase-return-list/purchase-return-list').then((m) => m.PurchaseReturnList),
        canActivate: [permissionGuard('Purchase.View')],
        data: { group: 'Purchases', page: 'Purchase Returns' },
      },
      {
        path: 'purchases/returns/:id',
        loadComponent: () => import('./features/purchases/purchase-returns/purchase-return-detail/purchase-return-detail').then((m) => m.PurchaseReturnDetail),
        canActivate: [permissionGuard('Purchase.View')],
        data: { group: 'Purchases', page: 'Return details' },
      },
      {
        path: 'purchases/:id/return',
        loadComponent: () => import('./features/purchases/purchase-returns/purchase-return-form/purchase-return-form').then((m) => m.PurchaseReturnForm),
        canActivate: [permissionGuard('Purchase.Return')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Purchases', page: 'Return items' },
      },
      {
        path: 'purchases/:id',
        loadComponent: () => import('./features/purchases/purchase-detail/purchase-detail').then((m) => m.PurchaseDetail),
        canActivate: [permissionGuard('Purchase.View')],
        data: { group: 'Purchases', page: 'Purchase details' },
      },

      {
        path: 'accounts/expenses',
        loadComponent: () => import('./features/accounts/expenses/expense-list/expense-list').then((m) => m.ExpenseList),
        canActivate: [permissionGuard('Expenses.View')],
        data: { group: 'Accounts', page: 'Expenses' },
      },
      {
        path: 'accounts/expenses/new',
        loadComponent: () => import('./features/accounts/expenses/expense-form/expense-form').then((m) => m.ExpenseForm),
        canActivate: [permissionGuard('Expenses.Create')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Accounts', page: 'New expense' },
      },
      {
        path: 'accounts/expenses/categories',
        loadComponent: () => import('./features/accounts/expenses/expense-categories/expense-categories').then((m) => m.ExpenseCategories),
        canActivate: [permissionGuard('Expenses.Categories')],
        data: { group: 'Accounts', page: 'Expense categories' },
      },
      {
        path: 'accounts/expenses/:id',
        loadComponent: () => import('./features/accounts/expenses/expense-detail/expense-detail').then((m) => m.ExpenseDetail),
        canActivate: [permissionGuard('Expenses.View')],
        data: { group: 'Accounts', page: 'Expense details' },
      },

      {
        path: 'accounts/cash-bank',
        loadComponent: () => import('./features/accounts/cash-bank/account-list/account-list').then((m) => m.AccountList),
        canActivate: [permissionGuard('CashBank.View')],
        data: { group: 'Accounts', page: 'Cash & Bank' },
      },
      {
        path: 'accounts/cash-bank/new',
        loadComponent: () => import('./features/accounts/cash-bank/account-form/account-form').then((m) => m.AccountForm),
        canActivate: [permissionGuard('CashBank.Manage')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Accounts', page: 'New account' },
      },
      {
        path: 'accounts/cash-bank/transfer',
        loadComponent: () => import('./features/accounts/cash-bank/transfer-form/transfer-form').then((m) => m.TransferForm),
        canActivate: [permissionGuard('CashBank.Transfer')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Accounts', page: 'Transfer money' },
      },
      {
        path: 'accounts/cash-bank/:id/edit',
        loadComponent: () => import('./features/accounts/cash-bank/account-form/account-form').then((m) => m.AccountForm),
        canActivate: [permissionGuard('CashBank.Manage')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Accounts', page: 'Edit account' },
      },
      {
        path: 'accounts/cash-bank/:id',
        loadComponent: () => import('./features/accounts/cash-bank/account-detail/account-detail').then((m) => m.AccountDetail),
        canActivate: [permissionGuard('CashBank.View')],
        data: { group: 'Accounts', page: 'Account history' },
      },
      {
        path: 'accounts/reports/purchase-payments',
        loadComponent: () => import('./features/accounts/reports/payment-report/payment-report').then((m) => m.PaymentReport),
        canActivate: [permissionGuard('Reports.Accounts')],
        data: { group: 'Accounts Reports', page: 'Purchase Payment Report', side: 'purchase' },
      },
      {
        path: 'accounts/reports/sales-payments',
        loadComponent: () => import('./features/accounts/reports/payment-report/payment-report').then((m) => m.PaymentReport),
        canActivate: [permissionGuard('Reports.Accounts')],
        data: { group: 'Accounts Reports', page: 'Sales Payment Report', side: 'sales' },
      },
      {
        path: 'accounts/reports/supplier-outstanding',
        loadComponent: () => import('./features/accounts/reports/outstanding-report/outstanding-report').then((m) => m.OutstandingReport),
        canActivate: [permissionGuard('Reports.Accounts')],
        data: { group: 'Accounts Reports', page: 'Supplier Outstanding', side: 'supplier' },
      },
      {
        path: 'accounts/reports/customer-outstanding',
        loadComponent: () => import('./features/accounts/reports/outstanding-report/outstanding-report').then((m) => m.OutstandingReport),
        canActivate: [permissionGuard('Reports.Accounts')],
        data: { group: 'Accounts Reports', page: 'Customer Outstanding', side: 'customer' },
      },
      {
        path: 'accounts/reports/expenses',
        loadComponent: () => import('./features/accounts/reports/expense-report/expense-report').then((m) => m.ExpenseReport),
        canActivate: [permissionGuard('Reports.Accounts')],
        data: { group: 'Accounts Reports', page: 'Expense Report' },
      },
      {
        path: 'accounts/reports/cash-flow',
        loadComponent: () => import('./features/accounts/reports/cash-flow-report/cash-flow-report').then((m) => m.CashFlowReport),
        canActivate: [permissionGuard('Reports.Accounts')],
        data: { group: 'Accounts Reports', page: 'Cash Flow' },
      },
      {
        path: 'accounts/reports/cash-bank',
        loadComponent: () => import('./features/accounts/reports/cash-bank-report/cash-bank-report').then((m) => m.CashBankReport),
        canActivate: [permissionGuard('Reports.Accounts')],
        data: { group: 'Accounts Reports', page: 'Cash & Bank Report' },
      },
      {
        path: 'accounts/reports/payment-methods',
        loadComponent: () => import('./features/accounts/reports/payment-method-report/payment-method-report').then((m) => m.PaymentMethodReport),
        canActivate: [permissionGuard('Reports.Accounts')],
        data: { group: 'Accounts Reports', page: 'Payment Method Summary' },
      },
      {
        path: 'accounts/financial-transactions',
        loadComponent: () => import('./features/accounts/financial-transactions/financial-transaction-list/financial-transaction-list').then((m) => m.FinancialTransactionList),
        canActivate: [permissionGuard('FinancialTransactions.View')],
        data: { group: 'Accounts', page: 'Financial Transactions' },
      },

      {
        path: 'sales/customers',
        component: CustomerList,
        canActivate: [permissionGuard('Customers.View')],
        data: { group: 'Sales & Billing', page: 'Customers' },
      },
      {
        path: 'sales/customers/new',
        component: CustomerForm,
        canActivate: [permissionGuard('Customers.Create')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Sales & Billing', page: 'New customer' },
      },
      {
        path: 'sales/customers/:id/edit',
        component: CustomerForm,
        canActivate: [permissionGuard('Customers.Edit')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Sales & Billing', page: 'Edit customer' },
      },

      {
        path: 'sales/new',
        component: Pos,
        canActivate: [permissionGuard('Sales.Create')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Sales & Billing', page: 'New sale' },
      },
      {
        path: 'sales',
        component: SalesList,
        canActivate: [permissionGuard('Sales.View')],
        data: { group: 'Sales & Billing', page: 'Sales' },
      },
      {
        path: 'sales/returns',
        loadComponent: () => import('./features/sales/sales-returns/sales-return-list/sales-return-list').then((m) => m.SalesReturnList),
        canActivate: [permissionGuard('Sales.View')],
        data: { group: 'Sales & Billing', page: 'Sales Returns' },
      },
      {
        path: 'sales/returns/:id',
        loadComponent: () => import('./features/sales/sales-returns/sales-return-detail/sales-return-detail').then((m) => m.SalesReturnDetail),
        canActivate: [permissionGuard('Sales.View')],
        data: { group: 'Sales & Billing', page: 'Return details' },
      },
      {
        path: 'sales/:id/return',
        loadComponent: () => import('./features/sales/sales-returns/sales-return-form/sales-return-form').then((m) => m.SalesReturnForm),
        canActivate: [permissionGuard('Sales.Return')],
        canDeactivate: [unsavedChangesGuard],
        data: { group: 'Sales & Billing', page: 'Return items' },
      },
      {
        path: 'sales/:id',
        loadComponent: () => import('./features/sales/sales-detail/sales-detail').then((m) => m.SalesDetail),
        canActivate: [permissionGuard('Sales.View')],
        data: { group: 'Sales & Billing', page: 'Invoice details' },
      },

      {
        path: 'reports/stock',
        component: CurrentStockReport,
        canActivate: [permissionGuard('Reports.Stock')],
        data: { group: 'Reports', page: 'Current Stock' },
      },
      {
        path: 'reports/stock-movement',
        component: StockMovementReport,
        canActivate: [permissionGuard('Reports.Movement')],
        data: { group: 'Reports', page: 'Stock Movement' },
      },
      {
        path: 'reports/purchases',
        component: PurchaseReport,
        canActivate: [permissionGuard('Reports.Purchase')],
        data: { group: 'Reports', page: 'Purchase Report' },
      },
      {
        path: 'reports/sales',
        component: SalesReport,
        canActivate: [permissionGuard('Reports.Sales')],
        data: { group: 'Reports', page: 'Sales Report' },
      },
      {
        path: 'reports/profit',
        component: ProfitSummaryReport,
        canActivate: [permissionGuard('Reports.Profit')],
        data: { group: 'Reports', page: 'Profit / Margin Summary' },
      },
    ],
  },
];
