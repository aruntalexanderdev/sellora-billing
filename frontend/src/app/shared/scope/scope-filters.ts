import { Component, input, model } from '@angular/core';
import { BranchSelect } from '../branches/branch-select';
import { LocationSelect } from '../locations/location-select';

/**
 * Branch and/or location filter fields for lists and reports. Each select hides itself when there is nothing to choose
 * (single branch; no `StockLocations.View`; <= 1 eligible location), so the host can always render this.
 * Place inside a `.report-filters` bar.
 */
@Component({
  selector: 'app-scope-filters',
  standalone: true,
  imports: [BranchSelect, LocationSelect],
  template: `
    @if (showBranch()) {
      <app-branch-select data-testid="branch-filter" [(value)]="branchId" />
    }
    @if (showLocation()) {
      <app-location-select data-testid="location-filter" purpose="filter" [branchId]="branchId()" [(value)]="stockLocationId" />
    }
  `,
  styleUrl: './scope-filters.scss',
})
export class ScopeFilters {
  readonly showBranch = input(true);
  readonly showLocation = input(true);
  /** Selected branch id; null = all branches. */
  readonly branchId = model<string | null>(null);
  /** Selected location id; null = all locations. */
  readonly stockLocationId = model<string | null>(null);
}
