import type { CurrencyItem, UpsertCurrencyRequest } from '../administration/currencies/currencies.models';
import type { UnitItem, UpsertUnitRequest } from '../masters/units/units.models';

export type { CurrencyItem, UpsertCurrencyRequest, UnitItem, UpsertUnitRequest };

/** Mirrors Api/Contracts/PlatformContracts.cs PlatformCompanyDto. */
export interface PlatformCompany {
  id: string;
  code: string;
  name: string;
  isActive: boolean;
  createdAt: string;
  memberCount: number;
  ownerCount: number;
}

/** Mirrors PlatformCurrencyDto (active currencies for the base-currency choice). */
export interface PlatformCurrencyOption {
  id: string;
  code: string;
  name: string;
  symbol: string;
}

/** Mirrors PlatformNewOwnerRequest. */
export interface PlatformNewOwner {
  firstName: string;
  lastName: string;
  username: string;
  email: string;
  phoneNumber: string | null;
  password: string;
  confirmPassword: string;
}

/** Mirrors PlatformOwnerRequest: exactly one of `userId` (existing user) or `newUser`. */
export interface PlatformOwner {
  userId: string | null;
  newUser: PlatformNewOwner | null;
}

/** Mirrors CreatePlatformCompanyRequest. */
export interface CreatePlatformCompanyRequest {
  code: string;
  name: string;
  businessName: string;
  invoicePrefix: string;
  baseCurrencyId: string;
  gstin: string | null;
  addressLine: string | null;
  city: string | null;
  state: string | null;
  phone: string | null;
  email: string | null;
  owner: PlatformOwner;
  reason: string | null;
}

/** Mirrors CreatedPlatformCompanyDto. */
export interface CreatedPlatformCompany {
  id: string;
  code: string;
  mainBranchId: string;
  ownerUserId: string;
}

/** Mirrors SetPlatformCompanyStatusRequest (reason required). */
export interface SetPlatformCompanyStatusRequest {
  isActive: boolean;
  reason: string;
}
