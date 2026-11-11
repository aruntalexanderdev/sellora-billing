import { GOV, type GovernanceMember } from './governance.models';
import { governanceCapabilities, grantableCodes, memberActions } from './governance.logic';

const holds = (...codes: string[]) => (code?: string | null) => !code || codes.includes(code);
const none = holds();
const member = (over: Partial<GovernanceMember> = {}): Pick<GovernanceMember, 'title' | 'status'> => ({ title: 'Director', status: 'Active', ...over });

describe('governance actions', () => {
  it('a view-only member sees no actions at all', () => {
    const has = holds(GOV.view);
    expect(memberActions(member(), has)).toEqual([]);
    expect(governanceCapabilities(has)).toMatchObject({ canAddMember: false, addableTitles: [], canManageAdministrators: false });
  });

  it('a member without any governance permission gets nothing', () => {
    expect(memberActions(member(), none)).toEqual([]);
    expect(governanceCapabilities(none).canAddMember).toBe(false);
  });

  it('ManageMembers manages non-owners (and may add them), never owners', () => {
    const has = holds(GOV.view, GOV.manageMembers);
    expect(memberActions(member({ title: 'Director' }), has)).toEqual(['changeTitle', 'end']);
    expect(memberActions(member({ title: 'Owner' }), has)).toEqual([]);
    expect(governanceCapabilities(has).addableTitles).toEqual(['BoardMember', 'Director', 'DelegatedAdministrator']);
  });

  it('permission grants need AssignPermissions on top; owners need ManageOwners', () => {
    expect(memberActions(member(), holds(GOV.manageMembers, GOV.assignPermissions))).toEqual(['changeTitle', 'setPermissions', 'end']);
    expect(memberActions(member({ title: 'Owner' }), holds(GOV.assignPermissions))).toEqual([]);
    expect(memberActions(member({ title: 'Owner' }), holds(GOV.manageOwners, GOV.assignPermissions))).toEqual(['changeTitle', 'setPermissions', 'end']);
  });

  it('ended members offer no actions', () => {
    expect(memberActions(member({ status: 'Ended' }), holds(GOV.manageMembers, GOV.manageOwners, GOV.assignPermissions))).toEqual([]);
  });

  it('administrator delegation needs ManageAdministrators', () => {
    expect(governanceCapabilities(holds(GOV.manageAdministrators)).canManageAdministrators).toBe(true);
    expect(governanceCapabilities(holds(GOV.manageMembers)).canManageAdministrators).toBe(false);
  });

  it('only codes the user holds can be granted', () => {
    const all = [GOV.view, GOV.manageMembers, GOV.manageOwners].map((code) => ({ code, description: '' }));
    expect(grantableCodes(all, holds(GOV.view, GOV.manageMembers)).map((p) => p.code)).toEqual([GOV.view, GOV.manageMembers]);
  });
});
