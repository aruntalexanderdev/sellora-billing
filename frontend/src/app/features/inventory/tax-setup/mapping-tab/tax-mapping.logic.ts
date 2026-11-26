import type { TaxAssignment, TaxEffective, TaxScope } from '../tax-setup.models';

export interface SourceChip {
  text: 'Set here' | 'Product override' | string;
  tone: 'here' | 'override' | 'inherited';
}

/** The chip of a mapping row: where its effective profile comes from. */
export function sourceChip(level: TaxScope, assignment: TaxAssignment | null, effective: TaxEffective | null): SourceChip {
  if (assignment) return level === 'Product' ? { text: 'Product override', tone: 'override' } : { text: 'Set here', tone: 'here' };
  if (!effective) return { text: 'Not mapped', tone: 'inherited' };
  const from = effective.source === 'Company' ? 'company default' : effective.source.toLowerCase();
  return { text: `From ${from}`, tone: 'inherited' };
}

/** "Sales GST 18%" / "Sales GST 18% · Purchase GST 5%" for a row. */
export function effectiveText(assignment: TaxAssignment | null, effective: TaxEffective | null): string {
  if (assignment) {
    const purchase = assignment.purchaseProfile ? ` · Purchase ${assignment.purchaseProfile.name}` : '';
    return `Sales ${assignment.salesProfile.name}${purchase}`;
  }
  if (!effective) return 'No profile applies';
  const same = effective.purchaseProfile.id === effective.salesProfile.id;
  return `Sales ${effective.salesProfile.name}${same ? '' : ` · Purchase ${effective.purchaseProfile.name}`}`;
}

/** Overrides shown per node before "show all". */
export const OVERRIDE_PAGE = 50;
