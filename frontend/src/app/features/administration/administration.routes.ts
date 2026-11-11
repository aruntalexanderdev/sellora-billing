import { Routes } from '@angular/router';
import { permissionGuard } from '../../core/auth/auth.guard';
import { unsavedChangesGuard } from '../../core/unsaved-changes.guard';

// D12/O7: unit and currency writes (create/edit/status) live only in the platform console; the company lists are read-only.
export const ADMINISTRATION_ROUTES: Routes = [
  {
    path: 'users',
    loadComponent: () => import('./users/user-list/user-list').then((m) => m.UserList),
    canActivate: [permissionGuard('Users.View')],
    data: { group: 'Administration', page: 'Users' },
  },
  {
    path: 'users/new',
    loadComponent: () => import('./users/user-form/user-form').then((m) => m.UserForm),
    canActivate: [permissionGuard('Users.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Administration', page: 'Invite user' },
  },
  {
    path: 'users/:id/edit',
    loadComponent: () => import('./users/user-form/user-form').then((m) => m.UserForm),
    canActivate: [permissionGuard('Users.Edit')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Administration', page: 'Edit user' },
  },
  {
    path: 'roles',
    loadComponent: () => import('./roles/role-list/role-list').then((m) => m.RoleList),
    canActivate: [permissionGuard(['Roles.View', 'Branch.Roles.View'])],
    data: { group: 'Administration', page: 'Roles & Permissions' },
  },
  {
    path: 'roles/new',
    loadComponent: () => import('./roles/role-form/role-form').then((m) => m.RoleForm),
    canActivate: [permissionGuard(['Roles.Create', 'Branch.Roles.Manage'])],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Administration', page: 'New role' },
  },
  {
    path: 'roles/:id/edit',
    loadComponent: () => import('./roles/role-form/role-form').then((m) => m.RoleForm),
    canActivate: [permissionGuard(['Roles.Edit', 'Branch.Roles.Manage'])],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Administration', page: 'Edit role' },
  },
  {
    path: 'permissions',
    loadComponent: () => import('./permissions/permission-list/permission-list').then((m) => m.PermissionList),
    canActivate: [permissionGuard('Permissions.View')],
    data: { group: 'Administration', page: 'Permissions' },
  },
  {
    path: 'units',
    loadComponent: () => import('../masters/units/unit-list/unit-list').then((m) => m.UnitList),
    canActivate: [permissionGuard('Masters.View')],
    data: { group: 'Administration', page: 'Units' },
  },
  {
    path: 'taxes',
    loadComponent: () => import('../masters/taxes/tax-list/tax-list').then((m) => m.TaxList),
    canActivate: [permissionGuard('Masters.View')],
    data: { group: 'Administration', page: 'Taxes' },
  },
  {
    path: 'taxes/new',
    loadComponent: () => import('../masters/taxes/tax-form/tax-form').then((m) => m.TaxForm),
    canActivate: [permissionGuard('Masters.Edit')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Administration', page: 'New tax' },
  },
  {
    path: 'taxes/:id/edit',
    loadComponent: () => import('../masters/taxes/tax-form/tax-form').then((m) => m.TaxForm),
    canActivate: [permissionGuard('Masters.Edit')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Administration', page: 'Edit tax' },
  },
  {
    path: 'currencies',
    loadComponent: () => import('./currencies/currency-list/currency-list').then((m) => m.CurrencyList),
    canActivate: [permissionGuard('Masters.View')],
    data: { group: 'Administration', page: 'Currencies' },
  },
  {
    path: 'exchange-rates',
    loadComponent: () => import('./exchange-rates/exchange-rate-list/exchange-rate-list').then((m) => m.ExchangeRateList),
    canActivate: [permissionGuard('Masters.View')],
    data: { group: 'Administration', page: 'Exchange rates' },
  },
  {
    path: 'exchange-rates/new',
    loadComponent: () => import('./exchange-rates/exchange-rate-form/exchange-rate-form').then((m) => m.ExchangeRateForm),
    canActivate: [permissionGuard('Masters.Edit')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Administration', page: 'New exchange rate' },
  },
  {
    path: 'exchange-rates/:id/edit',
    loadComponent: () => import('./exchange-rates/exchange-rate-form/exchange-rate-form').then((m) => m.ExchangeRateForm),
    canActivate: [permissionGuard('Masters.Edit')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Administration', page: 'Edit exchange rate' },
  },
  {
    path: 'settings',
    loadComponent: () => import('../masters/settings/settings-page/settings-page').then((m) => m.SettingsPage),
    canActivate: [permissionGuard('Masters.View')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Administration', page: 'Business Settings' },
  },
  {
    path: 'users/:id/access',
    loadComponent: () => import('./users/user-access/user-access').then((m) => m.UserAccessPage),
    // Users.View reads the access view and role assignments; the write actions are gated inside by Company.Users.ManageBranchAccess / *.AssignRoles.
    canActivate: [permissionGuard('Users.View')],
    data: { group: 'Administration', page: 'User access' },
  },
  {
    path: 'roles/branches/:branchId/members',
    loadComponent: () => import('./roles/branch-members/branch-members').then((m) => m.BranchMembers),
    canActivate: [permissionGuard(['Roles.View', 'Branch.Roles.View'])],
    data: { group: 'Administration', page: 'Branch members' },
  },
];
