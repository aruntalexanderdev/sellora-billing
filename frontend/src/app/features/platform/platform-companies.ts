import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { problemMessage, toProblem } from '../../core/http/api-errors';
import { ReasonPrompt } from '../administration/access-ui/reason-prompt';
import { UiButton } from '../../shared/ui/button/button';
import { UiDataTable } from '../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../shared/ui/toast/toast.service';
import type { PlatformCompany } from './platform.models';
import { PlatformService } from './platform.service';

const COLS: DataTableColumn[] = [
  { label: 'Code', align: 'left' },
  { label: 'Company', align: 'left' },
  { label: 'Members', align: 'right' },
  { label: 'Owners', align: 'right' },
  { label: 'Created', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

@Component({
  selector: 'app-platform-companies',
  standalone: true,
  imports: [UiButton, UiDataTable, ReasonPrompt],
  template: `
    <div class="page-head">
      <div>
        <h1>Companies</h1>
        <p>Every company on the platform. Deactivating a company blocks its users from signing in; no business data is shown here.</p>
      </div>
      <ui-button variant="primary" (onClick)="create()">+ New company</ui-button>
    </div>
    @if (target(); as t) {
      <app-reason-prompt
        [title]="(t.isActive ? 'Deactivate ' : 'Activate ') + t.name"
        [confirmLabel]="t.isActive ? 'Deactivate' : 'Activate'"
        [destructive]="t.isActive"
        [busy]="busy()"
        [error]="error()"
        (confirmed)="confirmStatus(t, $event ?? '')"
        (cancelled)="target.set(null)"
      />
    }
    <ui-data-table [cols]="cols" [rows]="rows()" [state]="state()" emptyTitle="No companies yet" emptyBody="Create the first company to get started." (retry)="load()" />
  `,
  styleUrl: './platform.scss',
})
export class PlatformCompanies implements OnInit {
  private readonly service = inject(PlatformService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<PlatformCompany[]>([]);
  readonly target = signal<PlatformCompany | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((c) => ({
      cells: [
        { kind: 'text', text: c.code, weight: '650' },
        { kind: 'text', text: c.name, weight: '600' },
        { kind: 'text', text: String(c.memberCount), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: String(c.ownerCount), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: new Date(c.createdAt).toLocaleDateString(), color: 'var(--color-text-muted)' },
        { kind: 'badge', text: c.isActive ? 'Active' : 'Inactive', tone: c.isActive ? 'good' : 'mute' },
        { kind: 'actions', actions: [{ label: c.isActive ? 'Deactivate' : 'Activate', onClick: () => this.askStatus(c) }] },
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.state.set('loading');
    this.service.companies().subscribe({
      next: (items) => {
        this.items.set(items);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  create(): void {
    void this.router.navigate(['/platform/companies/new']);
  }

  askStatus(c: PlatformCompany): void {
    this.error.set(null);
    this.target.set(c);
  }

  confirmStatus(c: PlatformCompany, reason: string): void {
    this.busy.set(true);
    this.service.setCompanyStatus(c.id, { isActive: !c.isActive, reason }).subscribe({
      next: () => {
        this.busy.set(false);
        this.target.set(null);
        this.toast.show(`${c.name} ${c.isActive ? 'deactivated' : 'activated'}.`);
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        this.error.set(problemMessage(toProblem(err)));
      },
    });
  }
}
