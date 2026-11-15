import { branchColumnVisible, placeLabel, scopeCells, scopeColumns } from './list-scope';

const ctx = (accessScope: string | null, branches: number) => ({
  accessScope: () => accessScope,
  allowedBranches: () => Array.from({ length: branches }),
});

describe('list scope rules', () => {
  it('shows the Branch column for company-wide users, multi-branch users only', () => {
    expect(branchColumnVisible(ctx('AssignedBranches', 1))).toBe(false);
    expect(branchColumnVisible(ctx('CompanyWide', 1))).toBe(true);
    expect(branchColumnVisible(ctx('AssignedBranches', 2))).toBe(true);
    expect(branchColumnVisible(ctx(null, 0))).toBe(false);
  });

  it('builds columns and cells in the same order', () => {
    expect(scopeColumns(false, false)).toEqual([]);
    expect(scopeColumns(true, true).map((c) => c.label)).toEqual(['Branch', 'Location']);
    const item = { branchCode: 'MAIN', branchName: 'Main', stockLocationCode: 'VAN1', stockLocationName: 'Van 1' };
    expect(scopeCells(item, true, true).map((c) => (c as { text: string }).text)).toEqual(['Main (MAIN)', 'Van 1 (VAN1)']);
    expect(scopeCells(item, false, true)).toHaveLength(1);
    expect(scopeCells(item, false, false)).toEqual([]);
  });

  it('labels places with name and code, falling back gracefully', () => {
    expect(placeLabel('S', 'Store')).toBe('Store (S)');
    expect(placeLabel('S', null)).toBe('S');
    expect(placeLabel(null, null)).toBe('—');
  });
});
