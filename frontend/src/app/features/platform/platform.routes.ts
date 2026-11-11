import { Routes } from '@angular/router';
import { unsavedChangesGuard } from '../../core/unsaved-changes.guard';
import { platformGuard } from '../../core/platform/platform.guard';

// D7: the platform console is a separate tree outside the company shell: own layout, own session (core/platform), no company token.
export const PLATFORM_ROUTES: Routes = [
  { path: 'login', loadComponent: () => import('./platform-login').then((m) => m.PlatformLogin) },
  {
    path: '',
    loadComponent: () => import('./platform-layout').then((m) => m.PlatformLayout),
    canActivate: [platformGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'companies' },
      { path: 'companies', loadComponent: () => import('./platform-companies').then((m) => m.PlatformCompanies) },
      {
        path: 'companies/new',
        loadComponent: () => import('./platform-company-form').then((m) => m.PlatformCompanyForm),
        canDeactivate: [unsavedChangesGuard],
      },
      { path: 'units', loadComponent: () => import('./platform-units').then((m) => m.PlatformUnits) },
      { path: 'currencies', loadComponent: () => import('./platform-currencies').then((m) => m.PlatformCurrencies) },
    ],
  },
];
