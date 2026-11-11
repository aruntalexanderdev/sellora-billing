export type ContextStatus = 'loading' | 'ready' | 'noBranch' | 'error';

export interface CompanyContextInfo {
  id: string;
  name: string;
}

/** Marker properties set on route guards so the context service can re-evaluate the active route after a branch switch. */
export interface GuardRequirement {
  permissionCode?: string;
  /** Any-of form: the route is accessible when at least one of these codes is held. */
  permissionAnyOf?: readonly string[];
  governanceCode?: string | null;
  requiresGovernance?: boolean;
}

export const BRANCH_KEY_PREFIX = 'sellora.branch';

/** D13: the explicit "All branches" choice (persisted as the branch preference; never a branch id, which are Guids). */
export const ALL_BRANCHES = 'all';
