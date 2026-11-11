import { Routes } from '@angular/router';
import { permissionGuard } from '../../core/auth/auth.guard';

export const REPORTS_ROUTES: Routes = [
  {
    path: 'stock',
    loadComponent: () => import('./current-stock-report/current-stock-report').then((m) => m.CurrentStockReport),
    canActivate: [permissionGuard('Reports.Stock')],
    data: { group: 'Reports', page: 'Current Stock' },
  },
  {
    path: 'stock-movement',
    loadComponent: () => import('./stock-movement-report/stock-movement-report').then((m) => m.StockMovementReport),
    canActivate: [permissionGuard('Reports.Movement')],
    data: { group: 'Reports', page: 'Stock Movement' },
  },
  {
    path: 'purchases',
    loadComponent: () => import('./purchase-report/purchase-report').then((m) => m.PurchaseReport),
    canActivate: [permissionGuard('Reports.Purchase')],
    data: { group: 'Reports', page: 'Purchase Report' },
  },
  {
    path: 'sales',
    loadComponent: () => import('./sales-report/sales-report').then((m) => m.SalesReport),
    canActivate: [permissionGuard('Reports.Sales')],
    data: { group: 'Reports', page: 'Sales Report' },
  },
  {
    path: 'profit',
    loadComponent: () => import('./profit-summary-report/profit-summary-report').then((m) => m.ProfitSummaryReport),
    canActivate: [permissionGuard('Reports.Profit')],
    data: { group: 'Reports', page: 'Profit / Margin Summary' },
  },
];
