export interface SidebarNavItem {
  label: string;
  routerLink: string;
  active?: boolean;
  badge?: string;
}

export interface SidebarNavGroup {
  label: string;
  items: SidebarNavItem[];
}
