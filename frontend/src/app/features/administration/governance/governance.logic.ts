import { GOV, GOVERNANCE_TITLES, type GovernanceMember, type GovernancePermission, type GovernanceTitle } from './governance.models';

/** UI hint predicate: does the signed-in user hold this governance permission? (ContextService.hasGovernance). */
export type HasGovernance = (code?: string | null) => boolean;

export type MemberAction = 'changeTitle' | 'setPermissions' | 'end';

export interface GovernanceCapabilities {
  /** May open the add-member form (and which titles it offers). */
  canAddMember: boolean;
  addableTitles: GovernanceTitle[];
  /** Administrator grant / remove. */
  canManageAdministrators: boolean;
}

/** Owner-title members are managed with ManageOwners, everyone else with ManageMembers. */
function managePermissionFor(title: GovernanceTitle): string {
  return title === 'Owner' ? GOV.manageOwners : GOV.manageMembers;
}

export function governanceCapabilities(has: HasGovernance): GovernanceCapabilities {
  const addableTitles = GOVERNANCE_TITLES.filter((t) => has(managePermissionFor(t)));
  return { canAddMember: addableTitles.length > 0, addableTitles, canManageAdministrators: has(GOV.manageAdministrators) };
}

/** Actions offered on a governance member row. Ended members offer none. The server decides; this only hides what would be refused. */
export function memberActions(member: Pick<GovernanceMember, 'title' | 'status'>, has: HasGovernance): MemberAction[] {
  if (member.status !== 'Active') return [];
  const manage = has(managePermissionFor(member.title));
  const actions: MemberAction[] = [];
  if (manage) actions.push('changeTitle');
  // Grants: AssignPermissions, and for an Owner target also ManageOwners.
  if (has(GOV.assignPermissions) && manage) actions.push('setPermissions');
  if (manage) actions.push('end');
  return actions;
}

/** A delegator can only grant codes it holds itself (governance_delegation_exceeded otherwise). */
export function grantableCodes(all: GovernancePermission[], has: HasGovernance): GovernancePermission[] {
  return all.filter((p) => has(p.code));
}

/** Titles an existing member can be changed to by this user. */
export function titleChoices(has: HasGovernance): GovernanceTitle[] {
  return governanceCapabilities(has).addableTitles;
}
