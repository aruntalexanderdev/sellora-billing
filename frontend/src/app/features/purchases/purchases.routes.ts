import { Routes } from '@angular/router';
import { permissionGuard } from '../../core/auth/auth.guard';
import { unsavedChangesGuard } from '../../core/unsaved-changes.guard';

export const PURCHASES_ROUTES: Routes = [
  {
    path: 'suppliers',
    loadComponent: () => import('./suppliers/supplier-list/supplier-list').then((m) => m.SupplierList),
    canActivate: [permissionGuard('Suppliers.View')],
    data: { group: 'Purchases', page: 'Suppliers' },
  },
  {
    path: 'suppliers/new',
    loadComponent: () => import('./suppliers/supplier-form/supplier-form').then((m) => m.SupplierForm),
    canActivate: [permissionGuard('Suppliers.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Purchases', page: 'New supplier' },
  },
  {
    path: 'suppliers/:id/edit',
    loadComponent: () => import('./suppliers/supplier-form/supplier-form').then((m) => m.SupplierForm),
    canActivate: [permissionGuard('Suppliers.Edit')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Purchases', page: 'Edit supplier' },
  },
  {
    path: '',
    loadComponent: () => import('./purchase-list/purchase-list').then((m) => m.PurchaseList),
    canActivate: [permissionGuard('Purchase.View')],
    data: { group: 'Purchases', page: 'Purchases' },
  },
  {
    path: 'new',
    loadComponent: () => import('./purchase-form/purchase-form').then((m) => m.PurchaseForm),
    canActivate: [permissionGuard('Purchase.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Purchases', page: 'New purchase' },
  },
  {
    path: 'returns',
    loadComponent: () => import('./purchase-returns/purchase-return-list/purchase-return-list').then((m) => m.PurchaseReturnList),
    canActivate: [permissionGuard('Purchase.View')],
    data: { group: 'Purchases', page: 'Purchase Returns' },
  },
  {
    path: 'returns/:id',
    loadComponent: () => import('./purchase-returns/purchase-return-detail/purchase-return-detail').then((m) => m.PurchaseReturnDetail),
    canActivate: [permissionGuard('Purchase.View')],
    data: { group: 'Purchases', page: 'Return details' },
  },
  {
    path: ':id/return',
    loadComponent: () => import('./purchase-returns/purchase-return-form/purchase-return-form').then((m) => m.PurchaseReturnForm),
    canActivate: [permissionGuard('Purchase.Return')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Purchases', page: 'Return items' },
  },
  {
    path: ':id',
    loadComponent: () => import('./purchase-detail/purchase-detail').then((m) => m.PurchaseDetail),
    canActivate: [permissionGuard('Purchase.View')],
    data: { group: 'Purchases', page: 'Purchase details' },
  },
];
