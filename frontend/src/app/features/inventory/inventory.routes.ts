import { Routes } from '@angular/router';
import { permissionGuard } from '../../core/auth/auth.guard';
import { unsavedChangesGuard } from '../../core/unsaved-changes.guard';

export const INVENTORY_ROUTES: Routes = [
  {
    path: 'products',
    loadComponent: () => import('./products/product-list/product-list').then((m) => m.ProductList),
    canActivate: [permissionGuard('Inventory.View')],
    data: { group: 'Inventory', page: 'Products' },
  },
  {
    path: 'products/new',
    loadComponent: () => import('./products/product-form/product-form').then((m) => m.ProductForm),
    canActivate: [permissionGuard('Inventory.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Inventory', page: 'New product' },
  },
  {
    path: 'products/:id/edit',
    loadComponent: () => import('./products/product-form/product-form').then((m) => m.ProductForm),
    canActivate: [permissionGuard('Inventory.Edit')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Inventory', page: 'Edit product' },
  },
  {
    path: 'stock',
    loadComponent: () => import('./stock/stock-overview/stock-overview').then((m) => m.StockOverview),
    canActivate: [permissionGuard('Inventory.View')],
    data: { group: 'Inventory', page: 'Stock Overview' },
  },
  {
    path: 'stock-adjustments',
    loadComponent: () => import('./stock-adjustments/stock-adjustment-list/stock-adjustment-list').then((m) => m.StockAdjustmentList),
    canActivate: [permissionGuard('Inventory.View')],
    data: { group: 'Inventory', page: 'Stock Adjustments' },
  },
  {
    path: 'stock-adjustments/new',
    loadComponent: () => import('./stock-adjustments/stock-adjustment-form/stock-adjustment-form').then((m) => m.StockAdjustmentForm),
    canActivate: [permissionGuard('Inventory.Adjust')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Inventory', page: 'Adjust Stock' },
  },
  {
    path: 'stock-adjustments/:id',
    loadComponent: () => import('./stock-adjustments/stock-adjustment-detail/stock-adjustment-detail').then((m) => m.StockAdjustmentDetail),
    canActivate: [permissionGuard('Inventory.View')],
    data: { group: 'Inventory', page: 'Stock Adjustment' },
  },
  {
    path: 'stock/:id',
    loadComponent: () => import('./stock/stock-detail/stock-detail').then((m) => m.StockDetail),
    canActivate: [permissionGuard('Inventory.View')],
    data: { group: 'Inventory', page: 'Stock Details' },
  },
  {
    path: 'categories',
    loadComponent: () => import('../masters/product-categories/product-categories').then((m) => m.ProductCategories),
    canActivate: [permissionGuard(['Inventory.View', 'Inventory.Categories'])],
    data: { group: 'Inventory', page: 'Product Categories' },
  },
  {
    path: 'pricing',
    loadComponent: () => import('./pricing/pricing-page/pricing-page').then((m) => m.PricingPage),
    canActivate: [permissionGuard('Inventory.Price')],
    data: { group: 'Inventory', page: 'Stock & Pricing' },
  },
  {
    path: 'stock-transfers',
    loadComponent: () => import('./stock-transfers/stock-transfer-list/stock-transfer-list').then((m) => m.StockTransferList),
    canActivate: [permissionGuard('StockTransfers.View')],
    data: { group: 'Inventory', page: 'Stock Transfers' },
  },
  {
    path: 'stock-transfers/new',
    loadComponent: () => import('./stock-transfers/stock-transfer-form/stock-transfer-form').then((m) => m.StockTransferForm),
    canActivate: [permissionGuard('StockTransfers.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Inventory', page: 'New transfer' },
  },
  {
    path: 'stock-transfers/:id/edit',
    loadComponent: () => import('./stock-transfers/stock-transfer-form/stock-transfer-form').then((m) => m.StockTransferForm),
    canActivate: [permissionGuard('StockTransfers.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Inventory', page: 'Edit transfer' },
  },
  {
    path: 'stock-transfers/:id',
    loadComponent: () => import('./stock-transfers/stock-transfer-detail/stock-transfer-detail').then((m) => m.StockTransferDetail),
    canActivate: [permissionGuard('StockTransfers.View')],
    data: { group: 'Inventory', page: 'Transfer details' },
  },
];
