import { Routes } from '@angular/router';
import { Shell } from './layout/shell/shell';
import { Login } from './features/auth/login/login';
import { ForgotPassword } from './features/auth/forgot-password/forgot-password';
import { authGuard } from './core/auth/auth.guard';

// D8: login / forgot-password / no-branch-access and the shell are eager; every feature area is a lazy `loadChildren`
// tree in features/<area>/<area>.routes.ts (URL paths are unchanged from before the split).
export const routes: Routes = [
  { path: 'login', component: Login },
  { path: 'forgot-password', component: ForgotPassword },
  {
    path: 'no-branch-access',
    loadComponent: () => import('./features/no-branch-access/no-branch-access').then((m) => m.NoBranchAccess),
  },
  // D7: the platform console has its own layout/session and sits outside the company shell.
  { path: 'platform', loadChildren: () => import('./features/platform/platform.routes').then((m) => m.PLATFORM_ROUTES) },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      {
        path: 'dashboard',
        loadComponent: () => import('./features/dashboard/dashboard-page/dashboard-page').then((m) => m.DashboardPage),
        data: { group: 'Dashboard', page: 'Dashboard' },
      },
      {
        path: 'ui-showcase',
        loadComponent: () => import('./features/ui-showcase/ui-showcase').then((m) => m.UiShowcase),
        data: { group: 'System', page: 'UI Kit' },
      },
      {
        path: 'profile',
        loadComponent: () => import('./features/profile/profile-page/profile-page').then((m) => m.ProfilePage),
        data: { group: 'Account', page: 'My Profile' },
      },
      {
        path: 'permission-denied',
        loadComponent: () => import('./features/permission-denied/permission-denied').then((m) => m.PermissionDenied),
        data: { group: 'System', page: 'Permission denied' },
      },

      { path: 'administration', loadChildren: () => import('./features/administration/administration.routes').then((m) => m.ADMINISTRATION_ROUTES) },
      { path: 'company', loadChildren: () => import('./features/company/company.routes').then((m) => m.COMPANY_ROUTES) },
      { path: 'inventory', loadChildren: () => import('./features/inventory/inventory.routes').then((m) => m.INVENTORY_ROUTES) },
      { path: 'purchases', loadChildren: () => import('./features/purchases/purchases.routes').then((m) => m.PURCHASES_ROUTES) },
      { path: 'sales', loadChildren: () => import('./features/sales/sales.routes').then((m) => m.SALES_ROUTES) },
      { path: 'accounts', loadChildren: () => import('./features/accounts/accounts.routes').then((m) => m.ACCOUNTS_ROUTES) },
      { path: 'reports', loadChildren: () => import('./features/reports/reports.routes').then((m) => m.REPORTS_ROUTES) },
    ],
  },
];
