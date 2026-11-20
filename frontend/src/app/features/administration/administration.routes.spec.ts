import { ADMINISTRATION_ROUTES } from './administration.routes';

describe('administration routes (D12/O7: units and currencies are platform-managed)', () => {
  const paths = ADMINISTRATION_ROUTES.map((r) => r.path ?? '');

  it.each(['units', 'currencies'])('%s exposes only the read-only list route', (area) => {
    const own = paths.filter((p) => p === area || p.startsWith(`${area}/`));
    expect(own).toEqual([area]);
  });
});

describe('administration routes (country-state-masters)', () => {
  it.each(['countries', 'states'])('%s is gated by Masters.View and has no separate write routes (writes follow canEdit)', (path) => {
    const own = ADMINISTRATION_ROUTES.filter((r) => r.path === path || r.path?.startsWith(`${path}/`));
    expect(own).toHaveLength(1);
    expect(own[0].canActivate).toHaveLength(1);
  });
});
