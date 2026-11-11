import { Routes } from '@angular/router';
import { unsavedChangesGuard } from '../../core/unsaved-changes.guard';
import { governanceGuard, permissionGuard } from '../../core/auth/auth.guard';

// WP-C replaces governance (swap `ComingSoon` for the real screen via loadComponent; keep the path and guard).
const comingSoon = () => import('../../shared/ui/coming-soon/coming-soon').then((m) => m.ComingSoon);

export const COMPANY_ROUTES: Routes = [
  {
    path: 'branches',
    loadComponent: () => import('./branches/branch-list/branch-list').then((m) => m.BranchList),
    canActivate: [permissionGuard('Company.Branches.View')],
    data: { group: 'Company', page: 'Branches' },
  },
  {
    path: 'branches/new',
    loadComponent: () => import('./branches/branch-form/branch-form').then((m) => m.BranchForm),
    canActivate: [permissionGuard('Company.Branches.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Company', page: 'Branches' },
  },
  {
    path: 'branches/:id/edit',
    loadComponent: () => import('./branches/branch-form/branch-form').then((m) => m.BranchForm),
    canActivate: [permissionGuard('Company.Branches.Edit')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Company', page: 'Branches' },
  },
  {
    path: 'warehouses',
    loadComponent: () => import('./warehouses/warehouse-list/warehouse-list').then((m) => m.WarehouseList),
    canActivate: [permissionGuard('Company.Warehouses.View')],
    data: { group: 'Company', page: 'Warehouses' },
  },
  {
    path: 'warehouses/new',
    loadComponent: () => import('./warehouses/warehouse-form/warehouse-form').then((m) => m.WarehouseForm),
    canActivate: [permissionGuard('Company.Warehouses.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Company', page: 'Warehouses' },
  },
  {
    path: 'warehouses/:id/edit',
    loadComponent: () => import('./warehouses/warehouse-form/warehouse-form').then((m) => m.WarehouseForm),
    canActivate: [permissionGuard('Company.Warehouses.Edit')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Company', page: 'Warehouses' },
  },
  {
    path: 'vehicles',
    loadComponent: () => import('./vehicles/vehicle-list/vehicle-list').then((m) => m.VehicleList),
    canActivate: [permissionGuard('Company.Vehicles.View')],
    data: { group: 'Company', page: 'Vehicles' },
  },
  {
    path: 'vehicles/new',
    loadComponent: () => import('./vehicles/vehicle-form/vehicle-form').then((m) => m.VehicleForm),
    canActivate: [permissionGuard('Company.Vehicles.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Company', page: 'Vehicles' },
  },
  {
    path: 'vehicles/:id/edit',
    loadComponent: () => import('./vehicles/vehicle-form/vehicle-form').then((m) => m.VehicleForm),
    canActivate: [permissionGuard('Company.Vehicles.Edit')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Company', page: 'Vehicles' },
  },
  {
    path: 'stock-locations',
    loadComponent: () => import('./stock-locations/stock-location-list/stock-location-list').then((m) => m.StockLocationList),
    canActivate: [permissionGuard('StockLocations.View')],
    data: { group: 'Company', page: 'Stock Locations' },
  },
  {
    path: 'governance',
    loadComponent: () => import('../administration/governance/governance-page').then((m) => m.GovernancePage),
    canActivate: [governanceGuard('Company.Governance.View')],
    data: { group: 'Company', page: 'Governance' },
  },
];
