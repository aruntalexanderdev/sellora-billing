/** Mirrors backend `TaxContracts.cs` / `TaxEnums.cs` (camelCase, enums as strings, dates yyyy-MM-dd). */

export type TaxComponentKind = 'Cgst' | 'Sgst' | 'Utgst' | 'Igst' | 'Cess' | 'Vat' | 'Other';
export type TaxCalculationMethod = 'Percent' | 'AmountPerBaseUnit';
export type TaxRegime = 'Gst' | 'Vat';
export type TaxTreatment = 'Taxable' | 'NilRated' | 'Exempt' | 'ZeroRated' | 'NonTaxable';
export type TaxApplicability = 'IntraState' | 'InterState' | 'Always';
export type TaxScope = 'Company' | 'Category' | 'Subcategory' | 'Product';

export const COMPONENT_KINDS: TaxComponentKind[] = ['Cgst', 'Sgst', 'Utgst', 'Igst', 'Cess', 'Vat', 'Other'];
export const TAX_TREATMENTS: { value: TaxTreatment; label: string }[] = [
  { value: 'Taxable', label: 'Taxable' },
  { value: 'NilRated', label: 'Nil rated' },
  { value: 'Exempt', label: 'Exempt' },
  { value: 'ZeroRated', label: 'Zero rated' },
  { value: 'NonTaxable', label: 'Non-taxable' },
];

export interface TaxComponent {
  id: string;
  countryId: string;
  countryCode: string;
  code: string;
  name: string;
  kind: TaxComponentKind;
  method: TaxCalculationMethod;
  isActive: boolean;
  /** A profile version or a document line uses it: kind and method are fixed. */
  isUsed: boolean;
}

export interface UpsertTaxComponentRequest {
  countryId?: string;
  code: string;
  name: string;
  kind: TaxComponentKind;
  method: TaxCalculationMethod;
}

export interface TaxVersionComponent {
  id: string;
  taxComponentId: string;
  componentCode: string;
  componentName: string;
  kind: TaxComponentKind;
  method: TaxCalculationMethod;
  applicability: TaxApplicability;
  ratePercent: number | null;
  amountPerBaseUnit: number | null;
  sortOrder: number;
}

export interface TaxVersion {
  id: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  totalPercent: number;
  isCurrent: boolean;
  /** A document line uses it: immutable. */
  isUsed: boolean;
  createdAt: string;
  components: TaxVersionComponent[];
}

export interface TaxProfile {
  id: string;
  countryId: string;
  countryCode: string;
  countryName: string;
  code: string;
  name: string;
  regime: TaxRegime;
  treatment: TaxTreatment;
  isActive: boolean;
  mirrorTaxId: string;
  isAssigned: boolean;
  versionCount: number;
  currentVersion: TaxVersion | null;
}

/** Detail: every version, newest first. */
export interface TaxProfileDetail extends TaxProfile {
  versions: TaxVersion[];
}

export interface TaxVersionComponentRequest {
  taxComponentId: string;
  applicability: TaxApplicability;
  ratePercent: number | null;
  amountPerBaseUnit: number | null;
}

export interface AddTaxVersionRequest {
  effectiveFrom: string;
  components: TaxVersionComponentRequest[];
}

export interface CreateTaxProfileRequest {
  countryId: string;
  code: string;
  name: string;
  regime: TaxRegime;
  treatment: TaxTreatment;
  initialVersion?: AddTaxVersionRequest | null;
}

export interface UpdateTaxProfileRequest {
  code: string;
  name: string;
}

export interface TaxSeedResult {
  countrySupported: boolean;
  componentsAdded: number;
  profilesAdded: number;
  profilesSkipped: number;
  companyDefaultSet: boolean;
}

export interface TaxProfileRef {
  id: string;
  code: string;
  name: string;
  regime: TaxRegime;
  treatment: TaxTreatment;
  isActive: boolean;
}

export interface TaxAssignment {
  id: string;
  scopeType: TaxScope;
  /** Null for the company default. */
  scopeId: string | null;
  scopeName: string;
  countryId: string;
  salesProfile: TaxProfileRef;
  /** Null = same as sales (BR7). */
  purchaseProfile: TaxProfileRef | null;
}

export interface UpsertTaxAssignmentRequest {
  scopeType: TaxScope;
  scopeId: string | null;
  countryId: string;
  salesProfileId: string;
  purchaseProfileId: string | null;
}

export interface TaxEffective {
  salesProfile: TaxProfileRef;
  purchaseProfile: TaxProfileRef;
  source: TaxScope;
  sourceName: string;
}

export interface TaxTreeProduct {
  productId: string;
  sku: string;
  name: string;
  assignment: TaxAssignment;
}

export interface TaxTreeSubcategory {
  id: string;
  code: string;
  name: string;
  hsnCode: string | null;
  isActive: boolean;
  productCount: number;
  overrideCount: number;
  assignment: TaxAssignment | null;
  effective: TaxEffective | null;
  overrides: TaxTreeProduct[];
}

export interface TaxTreeCategory {
  id: string;
  code: string;
  name: string;
  isActive: boolean;
  productCount: number;
  overrideCount: number;
  assignment: TaxAssignment | null;
  effective: TaxEffective | null;
  subcategories: TaxTreeSubcategory[];
  overrides: TaxTreeProduct[];
}

export interface TaxAssignmentTree {
  countryId: string;
  countryCode: string;
  countryName: string;
  companyDefault: TaxAssignment | null;
  categories: TaxTreeCategory[];
  uncategorisedOverrideCount: number;
  uncategorisedOverrides: TaxTreeProduct[];
}

export interface FieldWarning {
  field: string;
  message: string;
}

/** `GET tax-assignments/resolve`. `resolved` false: no profile applies (other fields null). */
export interface TaxResolution {
  resolved: boolean;
  countryId: string;
  salesProfile: TaxProfileRef | null;
  purchaseProfile: TaxProfileRef | null;
  purchaseSameAsSales: boolean;
  source: TaxScope | null;
  sourceName: string | null;
  hsnCode: string | null;
  hsnSource: string | null;
  hsnWarning: boolean;
  hsnWarningMessage: string | null;
}

export interface ResolveQuery {
  countryId?: string | null;
  productId?: string | null;
  categoryId?: string | null;
  subcategoryId?: string | null;
  hsnCode?: string | null;
  salesProfileId?: string | null;
  purchaseProfileId?: string | null;
}
