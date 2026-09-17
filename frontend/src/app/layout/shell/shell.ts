import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { Sidebar } from '../sidebar/sidebar';
import { Header } from '../header/header';
import { UiToastHost } from '../../shared/ui/toast/toast-host';
import { UiConfirmDialogHost } from '../../shared/ui/confirm-dialog/confirm-dialog-host';
import type { SidebarNavGroup } from '../sidebar/sidebar.types';

// Static placeholder — Phase 1 replaces this with permission-filtered nav from the authenticated user.
const NAV_GROUPS: SidebarNavGroup[] = [
  { label: 'Overview', items: [{ label: 'Dashboard', routerLink: '/dashboard' }] },
  {
    label: 'Administration',
    items: [
      { label: 'Users', routerLink: '/administration/users' },
      { label: 'Roles & Permissions', routerLink: '/administration/roles' },
    ],
  },
  {
    label: 'Inventory',
    items: [
      { label: 'Products', routerLink: '/inventory/products' },
      { label: 'Stock & Pricing', routerLink: '/inventory/pricing' },
      { label: 'Stock Movement', routerLink: '/inventory/movement' },
    ],
  },
  {
    label: 'Purchases',
    items: [
      { label: 'Suppliers', routerLink: '/purchases/suppliers' },
      { label: 'Purchase Entry', routerLink: '/purchases/new' },
    ],
  },
  {
    label: 'Sales & Billing',
    items: [
      { label: 'POS Billing', routerLink: '/sales/pos' },
      { label: 'Invoices', routerLink: '/sales/invoices' },
    ],
  },
  { label: 'Reports', items: [{ label: 'Reports', routerLink: '/reports' }] },
  { label: 'System', items: [{ label: 'UI Kit', routerLink: '/ui-showcase' }] },
];

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, Sidebar, Header, UiToastHost, UiConfirmDialogHost],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell {
  readonly navGroups = NAV_GROUPS;
}
