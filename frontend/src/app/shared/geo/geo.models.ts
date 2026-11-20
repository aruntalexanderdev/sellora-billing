import type { Observable } from 'rxjs';

/** Mirrors Api/Contracts/GeoContracts.cs (camelCase). */
export type StateKind = 'State' | 'UnionTerritory' | 'Emirate' | 'Region' | 'Governorate' | 'Municipality';

/** All six kinds must be offered (D31). */
export const STATE_KINDS: { value: StateKind; label: string }[] = [
  { value: 'State', label: 'State' },
  { value: 'UnionTerritory', label: 'Union territory' },
  { value: 'Emirate', label: 'Emirate' },
  { value: 'Region', label: 'Region' },
  { value: 'Governorate', label: 'Governorate' },
  { value: 'Municipality', label: 'Municipality' },
];

export const stateKindLabel = (k: StateKind): string => STATE_KINDS.find((x) => x.value === k)?.label ?? k;

export interface CountryDto {
  id: string;
  code: string;
  iso3Code: string | null;
  name: string;
  isActive: boolean;
  isSystem: boolean;
}

export interface CountryListDto {
  canEdit: boolean;
  items: CountryDto[];
}

export interface CountryLookupDto {
  id: string;
  code: string;
  name: string;
}

export interface StateDto {
  id: string;
  countryId: string;
  countryCode: string;
  countryName: string;
  code: string;
  name: string;
  kind: StateKind;
  gstStateCode: string | null;
  isActive: boolean;
  isSystem: boolean;
}

export interface StateListDto {
  canEdit: boolean;
  items: StateDto[];
}

export interface StateLookupDto {
  id: string;
  countryId: string;
  code: string;
  name: string;
  gstStateCode: string | null;
}

export interface CreateCountryRequest {
  code: string;
  iso3Code: string | null;
  name: string;
  isActive: boolean;
}

export interface UpdateCountryRequest {
  code: string;
  iso3Code: string | null;
  name: string;
}

export interface CreateStateRequest {
  countryId: string;
  code: string;
  name: string;
  kind: StateKind;
  gstStateCode: string | null;
  isActive: boolean;
}

export interface UpdateStateRequest {
  code: string;
  name: string;
  kind: StateKind;
  gstStateCode: string | null;
}

/** Where the Country -> State select pair loads its active options (company lookups or platform lookups). */
export interface GeoLookup {
  lookupCountries(): Observable<CountryLookupDto[]>;
  lookupStates(countryId: string): Observable<StateLookupDto[]>;
}

/** A stored link that may no longer be active: injected into the option lists so it stays displayed (BR5/AC6/D21). */
export interface GeoStoredLink {
  id: string;
  name: string;
}

/** What the masters screens need; the company and platform consoles each supply an adapter. */
export interface GeographyApi {
  listCountries(includeInactive: boolean): Observable<CountryListDto>;
  createCountry(r: CreateCountryRequest): Observable<CountryDto>;
  updateCountry(id: string, r: UpdateCountryRequest): Observable<CountryDto>;
  setCountryActive(id: string, isActive: boolean): Observable<void>;
  listStates(countryId: string | null, includeInactive: boolean): Observable<StateListDto>;
  createState(r: CreateStateRequest): Observable<StateDto>;
  updateState(id: string, r: UpdateStateRequest): Observable<StateDto>;
  setStateActive(id: string, isActive: boolean): Observable<void>;
  /** Active countries (state create/filter). */
  lookupCountries(): Observable<CountryLookupDto[]>;
}
