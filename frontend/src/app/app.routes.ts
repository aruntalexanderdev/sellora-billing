import { Routes } from '@angular/router';
import { Shell } from './layout/shell/shell';
import { UiShowcase } from './features/ui-showcase/ui-showcase';
import { Login } from './features/auth/login/login';
import { ForgotPassword } from './features/auth/forgot-password/forgot-password';
import { PermissionDenied } from './features/permission-denied/permission-denied';
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
import { SupplierList } from './features/purchases/suppliers/supplier-list/supplier-list';
import { SupplierForm } from './features/purchases/suppliers/supplier-form/supplier-form';
import { ProductList } from './features/inventory/products/product-list/product-list';
import { ProductForm } from './features/inventory/products/product-form/product-form';
import { PurchaseList } from './features/purchases/purchase-list/purchase-list';
import { PurchaseForm } from './features/purchases/purchase-form/purchase-form';
import { PurchaseDetail } from './features/purchases/purchase-detail/purchase-detail';
import { StockOverview } from './features/inventory/stock/stock-overview/stock-overview';
import { StockDetail } from './features/inventory/stock/stock-detail/stock-detail';
import { StockAdjustmentForm } from './features/inventory/stock-adjustments/stock-adjustment-form/stock-adjustment-form';
import { PricingPage } from './features/inventory/pricing/pricing-page/pricing-page';
import { CustomerList } from './features/sales/customers/customer-list/customer-list';
import { CustomerForm } from './features/sales/customers/customer-form/customer-form';
import { Pos } from './features/sales/pos/pos';
import { SalesList } from './features/sales/sales-list/sales-list';
import { SalesDetail } from './features/sales/sales-detail/sales-detail';
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
      { path: '', pathMatch: 'full', redirectTo: 'administration/users' },
      { path: 'ui-showcase', component: UiShowcase, data: { group: 'System', page: 'UI Kit' } },
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
        path: 'purchases/:id',
        component: PurchaseDetail,
        canActivate: [permissionGuard('Purchase.View')],
        data: { group: 'Purchases', page: 'Purchase details' },
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
        path: 'sales/:id',
        component: SalesDetail,
        canActivate: [permissionGuard('Sales.View')],
        data: { group: 'Sales & Billing', page: 'Invoice details' },
      },
    ],
  },
];
