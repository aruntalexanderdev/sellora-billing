import { Component, DestroyRef, Injector, computed, effect, inject, input, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { startWith } from 'rxjs';
import { GeoService } from './geo.service';
import type { CountryLookupDto, GeoLookup, GeoStoredLink, StateLookupDto } from './geo.models';

/**
 * Country -> State select pair (country-state-masters BR5/BR7, D6, D21).
 * - Options are the active lookups; a stored (possibly inactive) link is injected so it stays displayed.
 * - States are filtered by the selected country; changing/clearing the country clears the state.
 * - While no state is linked, the legacy free-text state is shown as a read-only hint (D6).
 * Controls hold the id, '' meaning "none". The host is `display: contents` so the two fields sit in the parent's grid.
 */
@Component({
  selector: 'app-geo-select',
  standalone: true,
  imports: [ReactiveFormsModule],
  styleUrls: ['./geo-select.scss'],
  host: { style: 'display: contents' },
  template: `
    <div>
      <label class="field-label" [for]="idPrefix() + '-country'">{{ countryLabel() }}</label>
      <select [id]="idPrefix() + '-country'" class="field-input" [formControl]="countryControl()" (change)="onCountryChange()" data-testid="geo-country">
        <option value="">Select a country…</option>
        @for (c of countryList(); track c.id) {
          <option [value]="c.id">{{ c.name }}</option>
        }
      </select>
      @if (countryControl().errors?.['server']) {
        <div class="field-error">{{ countryControl().errors?.['server'] }}</div>
      }
    </div>
    <div>
      <label class="field-label" [for]="idPrefix() + '-state'">{{ stateLabel() }}</label>
      <select [id]="idPrefix() + '-state'" class="field-input" [formControl]="stateControl()" data-testid="geo-state">
        <option value="">{{ countryId() ? 'Select a state…' : 'Select a country first' }}</option>
        @for (s of stateList(); track s.id) {
          <option [value]="s.id">{{ s.name }}</option>
        }
      </select>
      @if (showLegacy()) {
        <div class="field-hint" data-testid="geo-legacy">Previously entered: “{{ legacyState() }}” (not linked to a state). Choose a state to replace it.</div>
      }
      @if (stateControl().errors?.['server']) {
        <div class="field-error">{{ stateControl().errors?.['server'] }}</div>
      }
    </div>
  `,
})
export class GeoSelect {
  private readonly injector = inject(Injector);

  readonly countryControl = input.required<FormControl<string>>();
  readonly stateControl = input.required<FormControl<string>>();
  /** Lookup source; defaults to the company lookups (GeoService). The platform console passes its own. */
  readonly lookup = input<GeoLookup | null>(null);
  readonly storedCountry = input<GeoStoredLink | null>(null);
  readonly storedState = input<GeoStoredLink | null>(null);
  readonly legacyState = input<string | null>(null);
  readonly idPrefix = input('geo');
  readonly countryLabel = input('Country');
  readonly stateLabel = input('State');

  readonly countryId = signal('');
  private readonly stateId = signal('');
  private readonly countries = signal<CountryLookupDto[]>([]);
  private readonly states = signal<StateLookupDto[]>([]);

  private readonly source = computed<GeoLookup>(() => this.lookup() ?? this.injector.get(GeoService));

  readonly countryList = computed(() => {
    const base = this.countries();
    const stored = this.storedCountry();
    return stored && !base.some((c) => c.id === stored.id) ? [...base, { id: stored.id, code: '', name: stored.name }] : base;
  });

  readonly stateList = computed(() => {
    const base = this.states();
    const stored = this.storedState();
    const country = this.storedCountry();
    // The stored state belongs to the stored country; show it only while that country is selected.
    return stored && country && country.id === this.countryId() && !base.some((s) => s.id === stored.id)
      ? [...base, { id: stored.id, countryId: country.id, code: '', name: stored.name, gstStateCode: null }]
      : base;
  });

  readonly showLegacy = computed(() => !this.stateId() && !!this.legacyState()?.trim());

  constructor() {
    const destroyRef = inject(DestroyRef);
    effect((onCleanup) => {
      const cc = this.countryControl();
      const sc = this.stateControl();
      const subs = [
        cc.valueChanges.pipe(startWith(cc.value), takeUntilDestroyed(destroyRef)).subscribe((v) => this.countryId.set(v ?? '')),
        sc.valueChanges.pipe(startWith(sc.value), takeUntilDestroyed(destroyRef)).subscribe((v) => this.stateId.set(v ?? '')),
      ];
      onCleanup(() => subs.forEach((s) => s.unsubscribe()));
    });
    effect((onCleanup) => {
      const sub = this.source().lookupCountries().subscribe({ next: (c) => this.countries.set(c), error: () => this.countries.set([]) });
      onCleanup(() => sub.unsubscribe());
    });
    effect((onCleanup) => {
      const id = this.countryId();
      const source = this.source();
      this.states.set([]);
      if (!id) return;
      const sub = source.lookupStates(id).subscribe({ next: (s) => this.states.set(s), error: () => this.states.set([]) });
      onCleanup(() => sub.unsubscribe());
    });
  }

  /** User changed the country: the state no longer belongs to it, so clear it (BR7). */
  onCountryChange(): void {
    const state = this.stateControl();
    if (state.value) {
      state.setValue('');
      state.markAsDirty();
    }
  }
}
