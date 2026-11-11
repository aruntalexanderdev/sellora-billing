import { Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ALL_BRANCHES } from '../../core/context/context.models';
import type { AllowedBranch, CompanyOption } from '../../core/auth/auth.models';

@Component({
  selector: 'app-header',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './header.html',
  styleUrl: './header.scss',
})
export class Header {
  readonly userName = input('');
  readonly userRole = input('');
  readonly userInitials = input('');
  readonly companies = input<CompanyOption[]>([]);
  readonly companyId = input('');
  readonly branches = input<AllowedBranch[]>([]);
  readonly branchId = input('');
  /** D13: offer the "All branches" option (the shell passes `ALL_BRANCHES` as `branchId` while it is selected). */
  readonly canChooseAll = input(false);
  readonly allBranches = ALL_BRANCHES;
  readonly signOut = output<void>();
  readonly menuToggle = output<void>();
  readonly branchChange = output<string>();
  readonly companyChange = output<string>();

  /** Selectors are hidden when there is nothing to choose (1 branch and 1 company). */
  readonly showBranchSelect = computed(() => this.branches().length > 1 || this.canChooseAll());
  readonly showCompanySelect = computed(() => this.companies().length > 1);

  onBranch(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const next = select.value;
    // Keep showing the current branch until the switch is confirmed and completed.
    select.value = this.branchId();
    if (next && next !== this.branchId()) this.branchChange.emit(next);
  }

  onCompany(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const next = select.value;
    select.value = this.companyId();
    if (next && next !== this.companyId()) this.companyChange.emit(next);
  }
}
