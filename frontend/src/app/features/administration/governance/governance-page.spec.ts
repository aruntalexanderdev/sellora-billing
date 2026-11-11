import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ContextService } from '../../../core/context/context.service';
import { RolesService } from '../roles/roles.service';
import { UsersService } from '../users/users.service';
import { GOV, type GovernanceMember } from './governance.models';
import { GovernancePage } from './governance-page';
import { GovernanceService } from './governance.service';

const members: GovernanceMember[] = [
  { id: 'm1', userId: 'u1', displayName: 'Dana Director', title: 'Director', status: 'Active', startAt: '2026-01-01T00:00:00Z', endAt: null, endReason: null, permissions: [] },
];

function setup(held: string[]) {
  TestBed.configureTestingModule({
    imports: [GovernancePage],
    providers: [
      provideRouter([]),
      { provide: ContextService, useValue: { hasGovernance: (c?: string | null) => !c || held.includes(c) } },
      {
        provide: GovernanceService,
        useValue: {
          members: () => of(members), permissions: () => of([]), events: () => of({ items: [], total: 0, page: 1, pageSize: 20 }),
        },
      },
      { provide: UsersService, useValue: { list: () => of({ items: [], total: 0, page: 1, pageSize: 10 }) } },
      { provide: RolesService, useValue: { list: () => of([]) } },
    ],
  });
  const fixture = TestBed.createComponent(GovernancePage);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const text = () => el.textContent ?? '';
  return { fixture, el, text };
}

describe('GovernancePage action visibility', () => {
  it('without governance permissions shows the members read-only', () => {
    const { text, el } = setup([GOV.view]);
    expect(text()).toContain('Dana Director');
    expect(text()).not.toContain('+ Add member');
    expect(text()).not.toContain('Change title');
    expect(text()).not.toContain('Administrators');
    expect(Array.from(el.querySelectorAll('button')).map((b) => b.textContent?.trim())).not.toContain('End');
  });

  it('ManageMembers reveals add / change title / end', () => {
    const { text } = setup([GOV.view, GOV.manageMembers]);
    expect(text()).toContain('+ Add member');
    expect(text()).toContain('Change title');
    expect(text()).toContain('End');
  });

  it('the Administrators tab appears only with ManageAdministrators', () => {
    expect(setup([GOV.view, GOV.manageAdministrators]).text()).toContain('Administrators');
  });
});
