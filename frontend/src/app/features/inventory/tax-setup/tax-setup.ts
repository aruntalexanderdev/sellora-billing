import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { AuthService } from '../../../core/auth/auth.service';
import type { CountryLookupDto } from '../../../shared/geo/geo.models';
import { GeoService } from '../../../shared/geo/geo.service';
import { ComponentsTab } from './components-tab/components-tab';
import { MappingTab } from './mapping-tab/mapping-tab';
import { ProfilesTab } from './profiles-tab/profiles-tab';

export type TaxSetupTab = 'components' | 'profiles' | 'mapping';

/** Tax setup shell: tabs for components, profiles (with versions) and the product mapping. Reads need Tax.View, writes Tax.Manage. */
@Component({
  selector: 'app-tax-setup',
  standalone: true,
  imports: [ComponentsTab, ProfilesTab, MappingTab],
  styleUrl: './tax-setup.shared.scss',
  styles: [`
    .tabs { display: flex; gap: 4px; border-bottom: 1px solid var(--color-border); margin-bottom: 16px; overflow-x: auto; max-width: 100%; }
    .tab {
      border: 0; background: transparent; padding: 9px 14px; font-size: 13.5px; font-weight: 600; cursor: pointer;
      color: var(--color-text-muted); border-bottom: 2px solid transparent; white-space: nowrap;
    }
    .tab[aria-selected='true'] { color: var(--color-accent); border-bottom-color: var(--color-accent); }
  `],
  template: `
    <div class="page-head">
      <div>
        <h1>Tax setup</h1>
        <p>Components, rates by date, and which products use which profile.</p>
      </div>
    </div>

    <div class="tabs" role="tablist" aria-label="Tax setup">
      @for (t of tabs; track t.id) {
        <button type="button" role="tab" class="tab" [attr.aria-selected]="tab() === t.id" (click)="tab.set(t.id)" [attr.data-testid]="'tab-' + t.id">{{ t.label }}</button>
      }
    </div>

    @if (countriesLoaded()) {
      @switch (tab()) {
        @case ('components') { <app-tax-components-tab [countries]="countries()" [canManage]="canManage()" /> }
        @case ('profiles') { <app-tax-profiles-tab [countries]="countries()" [canManage]="canManage()" /> }
        @case ('mapping') { <app-tax-mapping-tab [countries]="countries()" [canManage]="canManage()" /> }
      }
    } @else {
      <p class="muted">Loading…</p>
    }
  `,
})
export class TaxSetup implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly geo = inject(GeoService);
  private readonly route = inject(ActivatedRoute);

  readonly tabs: { id: TaxSetupTab; label: string }[] = [
    { id: 'components', label: 'Components' },
    { id: 'profiles', label: 'Profiles' },
    { id: 'mapping', label: 'Mapping' },
  ];
  readonly tab = signal<TaxSetupTab>('profiles');
  readonly countries = signal<CountryLookupDto[]>([]);
  readonly countriesLoaded = signal(false);
  readonly canManage = computed(() => this.auth.hasPermission('Tax.Manage'));

  ngOnInit(): void {
    const requested = this.route.snapshot.queryParamMap.get('tab');
    if (requested === 'components' || requested === 'profiles' || requested === 'mapping') this.tab.set(requested);
    this.geo.lookupCountries().subscribe({
      next: (list) => {
        this.countries.set(list);
        this.countriesLoaded.set(true);
      },
      error: () => this.countriesLoaded.set(true),
    });
  }
}
