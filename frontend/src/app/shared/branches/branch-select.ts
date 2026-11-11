import { HttpClient } from '@angular/common/http';
import { Component, computed, effect, inject, input, model, signal } from '@angular/core';
import { environment } from '../../../environments/environment';
import { ContextService } from '../../core/context/context.service';

/** Mirrors Api/Contracts/BranchContracts.cs AccessibleBranchDto (GET /branches/accessible; needs no permission). */
export interface AccessibleBranch {
  id: string;
  code: string;
  name: string;
  isDefault: boolean;
}

/**
 * Branch filter dropdown for lists/reports (company-wide users). Emits the branch id, or `null` for "all branches".
 * Hidden when the caller can access a single branch (nothing to choose).
 */
@Component({
  selector: 'app-branch-select',
  standalone: true,
  template: `
    @if (visible()) {
      <select class="field-input" [attr.aria-label]="label()" [value]="value() ?? ''" (change)="onChange($any($event.target).value)">
        <option value="">{{ allLabel() }}</option>
        @for (b of branches(); track b.id) {
          <option [value]="b.id">{{ b.name }} ({{ b.code }})</option>
        }
      </select>
    }
  `,
})
export class BranchSelect {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ContextService);

  /** Selected branch id (two-way); null = all branches. */
  readonly value = model<string | null>(null);
  readonly label = input('Branch');
  readonly allLabel = input('All branches');

  readonly branches = signal<AccessibleBranch[]>([]);
  readonly visible = computed(() => this.branches().length > 1);

  constructor() {
    // Reload when the company/branch scope changes (branch switch, company switch).
    effect(() => {
      this.context.contextVersion();
      this.context.company();
      this.http.get<AccessibleBranch[]>(`${environment.apiBaseUrl}/branches/accessible`).subscribe({
        next: (items) => this.branches.set(items),
        error: () => this.branches.set([]),
      });
    });
  }

  onChange(raw: string): void {
    this.value.set(raw || null);
  }
}
