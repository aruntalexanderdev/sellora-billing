import type { TaxApplicability, TaxCalculationMethod, TaxComponentKind, TaxRegime, TaxTreatment } from './tax-setup.models';

/**
 * Client mirror of the backend version rules (BR3, TaxProfileService.ValidateSet / KindError) and T19.
 * The server stays authoritative; this only gives early feedback.
 */
export interface VersionLineDraft {
  code: string;
  kind: TaxComponentKind;
  method: TaxCalculationMethod;
  applicability: TaxApplicability;
  ratePercent: number | null;
  amountPerBaseUnit: number | null;
}

export const MAX_TOTAL_PERCENT = 100;

/** The only applicability the backend accepts for a component kind. */
export function defaultApplicability(kind: TaxComponentKind): TaxApplicability {
  if (kind === 'Cgst' || kind === 'Sgst' || kind === 'Utgst') return 'IntraState';
  if (kind === 'Igst') return 'InterState';
  return 'Always';
}

export function decimalPlaces(value: number): number {
  if (!Number.isFinite(value)) return 0;
  const text = String(value);
  if (text.includes('e-')) return Number(text.split('e-')[1]) + (text.split('e-')[0].split('.')[1]?.length ?? 0);
  return text.split('.')[1]?.length ?? 0;
}

/** Sum of the percentage lines that apply within one supply: intra-state and always. */
export function totalPercent(lines: readonly VersionLineDraft[]): number {
  const sum = lines
    .filter((l) => l.method === 'Percent' && (l.applicability === 'IntraState' || l.applicability === 'Always'))
    .reduce((s, l) => s + (l.ratePercent ?? 0), 0);
  return Math.round(sum * 1e6) / 1e6;
}

/** Splits a total GST rate into the CGST/SGST half and the IGST (= CGST + SGST). */
export function splitGstRate(total: number): { half: number; igst: number } {
  const half = Math.round((total / 2) * 1e6) / 1e6;
  return { half, igst: Math.round(half * 2 * 1e6) / 1e6 };
}

const eq = (a: number, b: number) => Math.abs(a - b) < 1e-9;

/** Errors of one version's line set (empty = fine). Mirrors BR3 for GST and a single VAT line for VAT. */
export function validateVersionLines(regime: TaxRegime, treatment: TaxTreatment, lines: readonly VersionLineDraft[]): string[] {
  if (treatment !== 'Taxable') {
    return lines.length > 0 ? ['A nil rated, exempt, zero-rated or non-taxable profile carries no tax components.'] : [];
  }
  if (lines.length === 0) return ['A taxable profile needs at least one tax component.'];

  const errors: string[] = [];
  lines.forEach((l, i) => {
    const row = `Row ${i + 1} (${l.code})`;
    if (l.method === 'Percent') {
      if (l.ratePercent === null || Number.isNaN(l.ratePercent)) errors.push(`${row}: enter the rate.`);
      else if (l.ratePercent < 0 || l.ratePercent > 100) errors.push(`${row}: the rate must be between 0 and 100.`);
      else if (decimalPlaces(l.ratePercent) > 4) errors.push(`${row}: the rate allows at most 4 decimal places.`);
    } else if (l.amountPerBaseUnit === null || Number.isNaN(l.amountPerBaseUnit)) {
      errors.push(`${row}: enter the amount per unit.`);
    } else if (l.amountPerBaseUnit < 0) {
      errors.push(`${row}: the amount per unit must be zero or more.`);
    } else if (decimalPlaces(l.amountPerBaseUnit) > 6) {
      errors.push(`${row}: the amount per unit allows at most 6 decimal places.`);
    }
  });
  if (errors.length > 0) return errors;

  const of = (pred: (l: VersionLineDraft) => boolean) => lines.filter(pred);
  const rate = (ls: VersionLineDraft[]) => ls.reduce((s, l) => s + (l.ratePercent ?? 0), 0);
  if (regime === 'Gst') {
    const cgst = of((l) => l.kind === 'Cgst');
    const state = of((l) => l.kind === 'Sgst' || l.kind === 'Utgst');
    const igst = of((l) => l.kind === 'Igst');
    if (cgst.length !== 1 || state.length !== 1) errors.push('An intra-state set needs exactly one CGST and one SGST (or UTGST) component.');
    else if (!eq(rate(cgst), rate(state))) errors.push('CGST and SGST (or UTGST) must have the same rate.');
    if (igst.length !== 1) errors.push('An inter-state set needs exactly one IGST component.');
    else if (cgst.length === 1 && state.length === 1 && !eq(rate(igst), rate(cgst) + rate(state))) {
      errors.push('IGST must equal CGST plus SGST (or UTGST).');
    }
  } else if (of((l) => l.kind === 'Vat').length !== 1) {
    errors.push('A taxable VAT profile needs exactly one VAT component.');
  }

  const total = totalPercent(lines);
  if (total <= 0) errors.push('A taxable profile needs a rate above zero. Use a nil rated or zero-rated profile for no tax.');
  else if (total > MAX_TOTAL_PERCENT) errors.push(`The total rate cannot exceed ${MAX_TOTAL_PERCENT}%.`);
  else if (decimalPlaces(total) > 3) errors.push('The total rate allows at most 3 decimal places.');
  return errors;
}

/** Local calendar date as yyyy-MM-dd. */
export function todayIso(now: Date = new Date()): string {
  const m = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  return `${now.getFullYear()}-${m}-${d}`;
}

/** T19: a version may not start after today; append-only: not before the latest version's start. ISO dates compare as strings. */
export function validateEffectiveFrom(from: string, today: string, latestFrom?: string | null): string | null {
  if (!from) return 'Choose the date the rate takes effect.';
  if (from > today) return `The version cannot start after today (${today}). Add it on the day it takes effect.`;
  if (latestFrom && from < latestFrom) return `The version must start on or after ${latestFrom}, the start of the latest version.`;
  return null;
}
