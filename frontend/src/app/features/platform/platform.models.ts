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

// ---- Database releases (docs/features/db-release-automation.md "API contract"; mirrors the backend DTO records) ----

export type DbReleaseTrigger = 'Deploy' | 'Retry';
export type DbReleaseRunStatus = 'Running' | 'Succeeded' | 'Failed';
export type DbScriptKind = 'EfMigration' | 'Data' | 'Repeatable' | 'Validation';
export type DbScriptStatus = 'Pending' | 'Succeeded' | 'Failed' | 'Baselined' | 'Passed' | 'Warning' | 'Removed';

export interface DbReleaseRunDto {
  id: string;
  environment: string;
  commitSha?: string | null;
  appVersion?: string | null;
  trigger: DbReleaseTrigger;
  triggeredBy?: string | null;
  retryReason?: string | null;
  status: DbReleaseRunStatus;
  startedAt: string;
  completedAt?: string | null;
  pipelineUrl?: string | null;
  summary?: string | null;
}

export interface DbScriptDto {
  id: string;
  name: string;
  kind: DbScriptKind;
  description?: string | null;
  status: DbScriptStatus;
  attemptCount: number;
  lastAttemptAt?: string | null;
  appliedAt?: string | null;
  lastDurationMs?: number | null;
  lastSqlState?: string | null;
  lastError?: string | null;
}

export interface DbScriptAttemptDto {
  id: string;
  scriptId: string;
  scriptName: string;
  scriptKind: DbScriptKind;
  releaseRunId: string;
  status: string;
  startedAt: string;
  durationMs?: number | null;
  rowsAffected?: number | null;
  sqlState?: string | null;
  errorMessage?: string | null;
  checksum?: string | null;
}

export interface DbReleaseRetryLastRequest {
  requestedBy: string;
  reason: string;
  requestedAt: string;
}

export interface DbReleaseRetryInfo {
  configured: boolean;
  canRetry: boolean;
  unavailableReason?: string | null;
  lastRequest?: DbReleaseRetryLastRequest | null;
}

export interface RetryDbReleaseRequest {
  environment: string;
  reason: string;
}

export interface DbReleaseRetryResponse {
  runId: string;
  pipelineUrl: string;
  requestedAt: string;
}

export interface DbReleasesOverviewDto {
  journalAvailable: boolean;
  runs: DbReleaseRunDto[];
  scripts: DbScriptDto[];
  retry: DbReleaseRetryInfo;
}

export interface DbReleaseRunDetailDto {
  run: DbReleaseRunDto;
  attempts: DbScriptAttemptDto[];
}

export interface DbScriptDetailDto {
  script: DbScriptDto;
  sql?: string | null;
  attempts: DbScriptAttemptDto[];
}
