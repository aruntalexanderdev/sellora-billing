import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { RolesService } from '../roles.service';
import type { BranchMember } from '../roles.models';

const COLS: DataTableColumn[] = [
  { label: 'Member', align: 'left' },
  { label: 'Username', align: 'left' },
  { label: 'Primary branch', align: 'left' },
  { label: 'Membership', align: 'left' },
  { label: '', align: 'right' },
];

/** Members allocated to one branch: who a branch administrator can assign branch roles to. */
@Component({
  selector: 'app-branch-members',
  standalone: true,
  imports: [UiDataTable, RouterLink],
  template: `
    <div class="page-head">
      <div>
        <h1>Branch members</h1>
        <p>People allocated to this branch. Open a member's roles to assign or end a branch role.</p>
      </div>
      <a routerLink="/administration/roles" class="muted">Back to roles</a>
    </div>
    <ui-data-table [cols]="cols" [rows]="rows()" [state]="state()" emptyTitle="No members" emptyBody="Nobody is allocated to this branch yet." (retry)="load()" />
  `,
  styleUrl: '../../access-ui/access-ui.scss',
})
export class BranchMembers implements OnInit {
  private readonly roles = inject(RolesService);
  private readonly branchId = inject(ActivatedRoute).snapshot.paramMap.get('branchId') ?? '';

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<BranchMember[]>([]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((m) => ({
      cells: [
        { kind: 'text', text: `${m.firstName} ${m.lastName}`.trim(), weight: '600' },
        { kind: 'text', text: m.username, color: 'var(--color-text-muted)' },
        { kind: 'badge', text: m.isPrimary ? 'Primary' : 'Secondary', tone: m.isPrimary ? 'info' : 'mute' },
        { kind: 'badge', text: m.membershipStatus, tone: m.membershipStatus === 'Active' ? 'good' : 'mute' },
        { kind: 'link', text: 'Roles', align: 'right', onClick: () => this.open(m) },
      ],
    })),
  );

  private readonly router = inject(Router);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.state.set('loading');
    this.roles.branchMembers(this.branchId).subscribe({
      next: (items) => {
        this.items.set(items);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  private open(m: BranchMember): void {
    void this.router.navigate(['/administration/users', m.userId, 'access'], { queryParams: { tab: 'roles' } });
  }
}
