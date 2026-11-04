import { vi } from 'vitest';
import { IN_USE_DEACTIVATE_TITLE, formatStandardFactor, statusAction } from './unit-list';

describe('formatStandardFactor', () => {
  it.each([
    ['Weight', 1, '1 kg'],
    ['Weight', 0.001, '0.001 kg'],
    ['Weight', 0.000001, '0.000001 kg'],
    ['Volume', 1, '1 L'],
    ['Volume', 0.001, '0.001 L'],
    ['Length', 0.01, '0.01 m'],
    ['Length', 1000, '1000 m'],
  ] as const)('%s with factor %s -> "%s"', (measurementCategory, standardFactor, expected) => {
    expect(formatStandardFactor({ measurementCategory, standardFactor })).toBe(expected);
  });

  it('is blank for a measurable unit without a factor', () => {
    expect(formatStandardFactor({ measurementCategory: 'Weight', standardFactor: null })).toBe('');
  });

  it.each(['Count', 'Packaging'] as const)('is blank for %s units, even with stray factor data', (measurementCategory) => {
    expect(formatStandardFactor({ measurementCategory, standardFactor: null })).toBe('');
    expect(formatStandardFactor({ measurementCategory, standardFactor: 12 })).toBe('');
  });
});

describe('statusAction', () => {
  it('disables Deactivate with an explanatory title for an active unit used by products', () => {
    const onToggle = vi.fn();
    const action = statusAction({ isActive: true, isInUse: true }, onToggle);
    expect(action.label).toBe('Deactivate');
    expect(action.disabled).toBe(true);
    expect(action.title).toBe(IN_USE_DEACTIVATE_TITLE);
    action.onClick();
    expect(onToggle).not.toHaveBeenCalled();
  });

  it('offers an enabled Deactivate for an active unit not in use', () => {
    const onToggle = vi.fn();
    const action = statusAction({ isActive: true, isInUse: false }, onToggle);
    expect(action).toMatchObject({ label: 'Deactivate' });
    expect(action.disabled).toBeFalsy();
    action.onClick();
    expect(onToggle).toHaveBeenCalledTimes(1);
  });

  it.each([true, false])('keeps Activate available for an inactive unit (isInUse=%s)', (isInUse) => {
    const onToggle = vi.fn();
    const action = statusAction({ isActive: false, isInUse }, onToggle);
    expect(action.label).toBe('Activate');
    expect(action.disabled).toBeFalsy();
    action.onClick();
    expect(onToggle).toHaveBeenCalledTimes(1);
  });
});
