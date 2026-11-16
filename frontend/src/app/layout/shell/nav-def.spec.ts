import { NAV_DEF, filterNav } from './nav-def';

const links = (groups: ReturnType<typeof filterNav>) => groups.flatMap((g) => g.items.map((i) => i.routerLink));
const gates = (perms: string[], gov: string[] = []) => ({
  hasPermission: (c: string) => perms.includes(c),
  hasGovernance: (c: string) => gov.includes(c),
});

describe('shell navigation', () => {
  it('lists every Phase 11 entry up front', () => {
    const all = NAV_DEF.flatMap((g) => g.items.map((i) => i.routerLink));
    for (const link of [
      '/company/branches', '/company/warehouses', '/company/vehicles', '/company/stock-locations', '/company/governance',
      '/inventory/stock-transfers', '/sales/van', '/administration/roles', '/administration/users',
    ]) {
      expect(all).toContain(link);
    }
  });

  it('shows only the always-visible entries without permissions', () => {
    expect(links(filterNav(NAV_DEF, gates([])))).toEqual(['/dashboard', '/ui-showcase']);
  });

  it('gates each new entry by its permission code', () => {
    const cases: [string, string][] = [
      ['Company.Branches.View', '/company/branches'],
      ['Company.Warehouses.View', '/company/warehouses'],
      ['Company.Vehicles.View', '/company/vehicles'],
      ['StockLocations.View', '/company/stock-locations'],
      ['StockTransfers.View', '/inventory/stock-transfers'],
      ['Shipments.View', '/inventory/shipments'],
      ['Shipments.View', '/inventory/stock-reservations'],
      ['StockTransfers.Receive', '/inventory/consignments'],
      ['Sales.Create', '/sales/van'],
    ];
    for (const [perm, link] of cases) {
      expect(links(filterNav(NAV_DEF, gates([perm])))).toContain(link);
      expect(links(filterNav(NAV_DEF, gates([])))).not.toContain(link);
    }
  });

  it('governance entry needs governance membership with the code, not a normal permission', () => {
    expect(links(filterNav(NAV_DEF, gates(['Company.Governance.View'])))).not.toContain('/company/governance');
    expect(links(filterNav(NAV_DEF, gates([], ['Company.Governance.View'])))).toContain('/company/governance');
  });

  it('drops groups that end up empty', () => {
    const groups = filterNav(NAV_DEF, gates(['Company.Branches.View']));
    expect(groups.map((g) => g.label)).toEqual(['Overview', 'Company', 'System']);
  });

  it('supports any-of permission lists', () => {
    const def = [{ label: 'G', items: [{ label: 'X', routerLink: '/x', permission: ['A', 'B'] }] }];
    expect(links(filterNav(def, gates(['B'])))).toEqual(['/x']);
    expect(links(filterNav(def, gates(['C'])))).toEqual([]);
  });
});
