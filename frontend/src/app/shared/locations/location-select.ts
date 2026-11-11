import { Component, computed, effect, inject, input, model, signal } from '@angular/core';
import { AuthService } from '../../core/auth/auth.service';
import { ContextService } from '../../core/context/context.service';
import { LocationPurpose, StockLocation, isEligibleLocation } from './locations.models';
import { StockLocationsService } from './stock-locations.service';

/**
 * D10: location picker. Rendered only for holders of `StockLocations.View` and only when more than one eligible
 * location exists; otherwise `value` stays null and the form omits `stockLocationId` (server default).
 * Default selection = the working branch's BranchStore.
 */
@Component({
  selector: 'app-location-select',
  standalone: true,
  template: `
    @if (visible()) {
      <select class="field-input" [attr.aria-label]="label()" [value]="value() ?? ''" (change)="onChange($any($event.target).value)">
        @if (purpose() === 'filter') {
          <option value="">{{ allLabel() }}</option>
        }
        @for (l of locations(); track l.id) {
          <option [value]="l.id" [selected]="l.id === value()">{{ l.name }} ({{ l.code }})</option>
        }
      </select>
    }
  `,
})
export class LocationSelect {
  private readonly service = inject(StockLocationsService);
  private readonly auth = inject(AuthService);
  private readonly context = inject(ContextService);

  readonly purpose = input.required<LocationPurpose>();
  /** Restrict to one branch (e.g. a document's branch); defaults to no restriction. */
  readonly branchId = input<string | null>(null);
  readonly label = input('Location');
  readonly allLabel = input('All locations');
  /** Selected location id (two-way); null = server default / all (filter). */
  readonly value = model<string | null>(null);

  readonly locations = signal<StockLocation[]>([]);
  readonly visible = computed(() => this.auth.hasPermission('StockLocations.View') && this.locations().length > 1);

  constructor() {
    effect(() => {
      this.context.contextVersion();
      const purpose = this.purpose();
      const branchId = this.branchId();
      if (!this.auth.hasPermission('StockLocations.View')) {
        this.locations.set([]);
        return;
      }
      this.service.list(branchId ? { branchId } : {}).subscribe({
        next: (all) => {
          const eligible = all.filter((l) => l.isActive && isEligibleLocation(l, purpose));
          this.locations.set(eligible);
          const current = this.value();
          if (current && !eligible.some((l) => l.id === current)) this.value.set(null);
          if (!this.value() && purpose !== 'filter' && eligible.length > 1) {
            this.value.set(this.defaultLocation(eligible)?.id ?? null);
          }
        },
        error: () => this.locations.set([]),
      });
    });
  }

  onChange(raw: string): void {
    this.value.set(raw || null);
  }

  private defaultLocation(eligible: StockLocation[]): StockLocation | undefined {
    const branchId = this.branchId() ?? this.context.workingBranch()?.id ?? null;
    return eligible.find((l) => l.locationType === 'BranchStore' && l.branchId === branchId) ?? eligible[0];
  }
}
