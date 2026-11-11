import { ADMINISTRATION_ROUTES } from './administration.routes';

describe('administration routes (D12/O7: units and currencies are platform-managed)', () => {
  const paths = ADMINISTRATION_ROUTES.map((r) => r.path ?? '');

  it.each(['units', 'currencies'])('%s exposes only the read-only list route', (area) => {
    const own = paths.filter((p) => p === area || p.startsWith(`${area}/`));
    expect(own).toEqual([area]);
  });
});
