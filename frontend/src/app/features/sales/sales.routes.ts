import { Routes } from '@angular/router';
import { permissionGuard } from '../../core/auth/auth.guard';
import { unsavedChangesGuard } from '../../core/unsaved-changes.guard';

export const SALES_ROUTES: Routes = [
  {
    path: 'customers',
    loadComponent: () => import('./customers/customer-list/customer-list').then((m) => m.CustomerList),
    canActivate: [permissionGuard('Customers.View')],
    data: { group: 'Sales & Billing', page: 'Customers' },
  },
  {
    path: 'customers/new',
    loadComponent: () => import('./customers/customer-form/customer-form').then((m) => m.CustomerForm),
    canActivate: [permissionGuard('Customers.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Sales & Billing', page: 'New customer' },
  },
  {
    path: 'customers/:id/edit',
    loadComponent: () => import('./customers/customer-form/customer-form').then((m) => m.CustomerForm),
    canActivate: [permissionGuard('Customers.Edit')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Sales & Billing', page: 'Edit customer' },
  },
  {
    path: 'new',
    loadComponent: () => import('./pos/pos').then((m) => m.Pos),
    canActivate: [permissionGuard('Sales.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Sales & Billing', page: 'New sale' },
  },
  {
    path: '',
    loadComponent: () => import('./sales-list/sales-list').then((m) => m.SalesList),
    canActivate: [permissionGuard('Sales.View')],
    data: { group: 'Sales & Billing', page: 'Sales' },
  },
  {
    path: 'van',
    loadComponent: () => import('./van/van-workspace/van-workspace').then((m) => m.VanWorkspace),
    canActivate: [permissionGuard('Sales.Create')],
    data: { group: 'Sales & Billing', page: 'Van workspace' },
  },
  {
    path: 'returns',
    loadComponent: () => import('./sales-returns/sales-return-list/sales-return-list').then((m) => m.SalesReturnList),
    canActivate: [permissionGuard('Sales.View')],
    data: { group: 'Sales & Billing', page: 'Sales Returns' },
  },
  {
    path: 'returns/:id',
    loadComponent: () => import('./sales-returns/sales-return-detail/sales-return-detail').then((m) => m.SalesReturnDetail),
    canActivate: [permissionGuard('Sales.View')],
    data: { group: 'Sales & Billing', page: 'Return details' },
  },
  {
    path: ':id/return',
    loadComponent: () => import('./sales-returns/sales-return-form/sales-return-form').then((m) => m.SalesReturnForm),
    canActivate: [permissionGuard('Sales.Return')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Sales & Billing', page: 'Return items' },
  },
  {
    path: ':id',
    loadComponent: () => import('./sales-detail/sales-detail').then((m) => m.SalesDetail),
    canActivate: [permissionGuard('Sales.View')],
    data: { group: 'Sales & Billing', page: 'Invoice details' },
  },
];
