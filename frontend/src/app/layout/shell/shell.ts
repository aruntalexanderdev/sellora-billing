import { Component, computed, inject } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';
import { Sidebar } from '../sidebar/sidebar';
import { Header } from '../header/header';
import { UiToastHost } from '../../shared/ui/toast/toast-host';
import { UiConfirmDialogHost } from '../../shared/ui/confirm-dialog/confirm-dialog-host';
import { AuthService } from '../../core/auth/auth.service';
import { BreadcrumbService } from '../../core/breadcrumb.service';
import type { SidebarNavGroup } from '../sidebar/sidebar.types';

interface NavItemDef {
  label: string;
  routerLink: string;
  permission: string;
}

// Only routes that exist today are listed here. Modules from later phases (Reports, Dashboard) are
// added once their screens actually exist, rather than showing dead links gated by permissions no
// screen yet checks.
const NAV_DEF: { label: string; items: NavItemDef[] }[] = [
  {
    label: 'Administration',
    items: [
      { label: 'Users', routerLink: '/administration/users', permission: 'Users.View' },
      { label: 'Roles & Permissions', routerLink: '/administration/roles', permission: 'Roles.View' },
      { label: 'Permissions', routerLink: '/administration/permissions', permission: 'Permissions.View' },
      { label: 'Units', routerLink: '/administration/units', permission: 'Masters.View' },
      { label: 'Taxes', routerLink: '/administration/taxes', permission: 'Masters.View' },
      { label: 'Business Settings', routerLink: '/administration/settings', permission: 'Masters.View' },
    ],
  },
  {
    label: 'Inventory',
    items: [
      { label: 'Stock Overview', routerLink: '/inventory/stock', permission: 'Inventory.View' },
      { label: 'Products', routerLink: '/inventory/products', permission: 'Inventory.View' },
      { label: 'Stock & Pricing', routerLink: '/inventory/pricing', permission: 'Inventory.Price' },
    ],
  },
  {
    label: 'Purchases',
    items: [
      { label: 'Purchases', routerLink: '/purchases', permission: 'Purchase.View' },
      { label: 'Suppliers', routerLink: '/purchases/suppliers', permission: 'Suppliers.View' },
    ],
  },
  {
    label: 'Sales & Billing',
    items: [
      { label: 'New Sale', routerLink: '/sales/new', permission: 'Sales.Create' },
      { label: 'Sales', routerLink: '/sales', permission: 'Sales.View' },
      { label: 'Customers', routerLink: '/sales/customers', permission: 'Customers.View' },
    ],
  },
  {
    label: 'Reports',
    items: [
      { label: 'Current Stock', routerLink: '/reports/stock', permission: 'Reports.Stock' },
      { label: 'Stock Movement', routerLink: '/reports/stock-movement', permission: 'Reports.Movement' },
      { label: 'Purchase Report', routerLink: '/reports/purchases', permission: 'Reports.Purchase' },
      { label: 'Sales Report', routerLink: '/reports/sales', permission: 'Reports.Sales' },
      { label: 'Profit / Margin', routerLink: '/reports/profit', permission: 'Reports.Profit' },
    ],
  },
  { label: 'System', items: [{ label: 'UI Kit', routerLink: '/ui-showcase', permission: '' }] },
];

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, Sidebar, Header, UiToastHost, UiConfirmDialogHost],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  readonly breadcrumb = inject(BreadcrumbService);

  readonly currentUser = this.auth.currentUser;
  readonly userInitials = computed(() => {
    const u = this.currentUser();
    return u ? `${u.firstName[0] ?? ''}${u.lastName[0] ?? ''}` : '';
  });

  readonly navGroups = computed<SidebarNavGroup[]>(() =>
    NAV_DEF.map((g) => ({
      label: g.label,
      items: g.items.filter((it) => !it.permission || this.auth.hasPermission(it.permission)),
    })).filter((g) => g.items.length > 0),
  );

  signOut(): void {
    this.auth.logout();
    this.router.navigateByUrl('/login');
  }
}
