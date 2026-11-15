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

/** Legacy persisted "All branches" preference (removed in FI-D11); migrated to the primary/default branch on hydrate. */
export const ALL_BRANCHES_LEGACY = 'all';
