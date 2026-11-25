import type { FieldWarning } from '../../features/inventory/tax-setup/tax-setup.models';

export type { FieldWarning };

/** Toast text for a save that succeeded: the base text plus the returned warnings, and a longer duration while warnings are shown. */
export function savedToast(base: string, warnings: readonly FieldWarning[] | null | undefined): { text: string; durationMs: number } {
  const list = (warnings ?? []).map((w) => w.message).filter((m) => !!m);
  return list.length === 0 ? { text: base, durationMs: 3200 } : { text: `${base} ${list.join(' ')}`, durationMs: 9000 };
}
