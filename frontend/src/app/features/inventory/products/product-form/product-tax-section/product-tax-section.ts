import { Component, DestroyRef, OnInit, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subject, catchError, of, switchMap } from 'rxjs';
import { AuthService } from '../../../../../core/auth/auth.service';
import { HSN_MESSAGE, hsnOrNull, hsnValidator, isValidHsn } from '../../../../../shared/tax/hsn';
import type { ResolveQuery, TaxProfile, TaxResolution } from '../../../tax-setup/tax-setup.models';
import { TaxSetupService } from '../../../tax-setup/tax-setup.service';

/** The `tax` sub-form of the product form. */
export type ProductTaxGroup = FormGroup<{
  overrideEnabled: FormControl<boolean>;
  salesProfileId: FormControl<string>;
  purchaseSameAsSales: FormControl<boolean>;
  purchaseProfileId: FormControl<string>;
  hsnCode: FormControl<string>;
  /** '' = company default, 'true' = prices include tax, 'false' = exclude. */
  priceBasis: FormControl<'' | 'true' | 'false'>;
}>;

export function createProductTaxGroup(): ProductTaxGroup {
  const group: ProductTaxGroup = new FormGroup({
    overrideEnabled: new FormControl(false, { nonNullable: true }),
    salesProfileId: new FormControl('', { nonNullable: true }),
    purchaseSameAsSales: new FormControl(true, { nonNullable: true }),
    purchaseProfileId: new FormControl('', { nonNullable: true }),
    hsnCode: new FormControl('', { nonNullable: true, validators: [hsnValidator] }),
    priceBasis: new FormControl<'' | 'true' | 'false'>('', { nonNullable: true }),
  });
  // An override needs its profile(s); the validators follow the toggles.
  group.valueChanges.subscribe(() => {
    const v = group.getRawValue();
    setRequired(group.controls.salesProfileId, v.overrideEnabled);
    setRequired(group.controls.purchaseProfileId, v.overrideEnabled && !v.purchaseSameAsSales);
  });
  return group;
}

function setRequired(control: FormControl<string>, required: boolean): void {
  if (control.hasValidator(Validators.required) === required) return;
  if (required) control.addValidators(Validators.required);
  else control.removeValidators(Validators.required);
  control.updateValueAndValidity({ emitEvent: false });
}

/** Tax fields of the upsert request; override ids are always present (null removes a stored override). */
export function taxRequestFields(group: ProductTaxGroup) {
  const v = group.getRawValue();
  const override = v.overrideEnabled && !!v.salesProfileId;
  return {
    hsnCode: hsnOrNull(v.hsnCode),
    priceIncludesTax: v.priceBasis === '' ? null : v.priceBasis === 'true',
    salesTaxProfileId: override ? v.salesProfileId : null,
    purchaseTaxProfileId: override && !v.purchaseSameAsSales ? v.purchaseProfileId || null : null,
  };
}

/** Maps a server error key onto a path inside the form (`tax.*`), or null when it is not a tax key. */
export function taxProblemPath(key: string): string | null {
  const k = key.charAt(0).toLowerCase() + key.slice(1);
  switch (k) {
    case 'hsnCode': return 'tax.hsnCode';
    case 'salesTaxProfileId': return 'tax.salesProfileId';
    case 'purchaseTaxProfileId': return 'tax.purchaseProfileId';
    case 'taxProfile': return 'tax';
    default: return null;
  }
}

/**
 * Tax block of the product form: the profile the product resolves to and where it comes from (BR8), an optional override,
 * HSN with the inherited code as placeholder and the T11 warning, and the price basis with the T16 notice.
 */
@Component({
  selector: 'app-product-tax-section',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink],
  styleUrl: './product-tax-section.scss',
  template: `
    <section class="tax-section" [formGroup]="group()" aria-label="Tax">
      <h2 class="section-title">Tax</h2>

      <div class="resolved" data-testid="tax-resolved" aria-live="polite">
        @if (resolution(); as r) {
          @if (r.resolved) {
            <div>
              <span class="strong">Sales:</span> {{ r.salesProfile?.name }}
              <span class="chip" data-testid="tax-source">{{ sourceText() }}</span>
            </div>
            <div class="muted">
              <span class="strong">Purchase:</span> {{ r.purchaseSameAsSales ? 'Same as sales' : r.purchaseProfile?.name }}
            </div>
          } @else {
            <div class="warn" data-testid="tax-unresolved">
              No tax profile applies yet.
              @if (canViewTax()) { Set a company default or a category mapping in <a routerLink="/inventory/tax-setup">Tax setup</a>, or override it below. }
              @else { Ask an administrator to set a company default, or override it below. }
            </div>
          }
        } @else {
          <span class="muted">Checking tax profile…</span>
        }
      </div>
      @if (group().errors?.['server']; as msg) { <div class="field-error" data-testid="tax-profile-error">{{ msg }}</div> }

      <label class="check">
        <input type="checkbox" formControlName="overrideEnabled" data-testid="override-toggle" />
        Override the tax profile for this product
      </label>

      @if (group().controls.overrideEnabled.value) {
        <div class="grid-2 override" data-testid="override-fields">
          <div>
            <label class="field-label" for="pt-sales">Sales tax profile</label>
            <select id="pt-sales" class="field-input" formControlName="salesProfileId">
              <option value="" disabled>Select a profile…</option>
              @for (p of options(group().controls.salesProfileId.value); track p.id) { <option [value]="p.id">{{ p.name }}</option> }
            </select>
            @if (group().controls.salesProfileId.errors?.['server']; as msg) { <div class="field-error">{{ msg }}</div> }
            @else if (group().controls.salesProfileId.touched && !group().controls.salesProfileId.value) { <div class="field-error">Select a profile or switch the override off.</div> }
          </div>
          <div>
            <label class="field-label">Purchase tax profile</label>
            <label class="check">
              <input type="checkbox" formControlName="purchaseSameAsSales" data-testid="purchase-same" />
              Same as sales
            </label>
            @if (!group().controls.purchaseSameAsSales.value) {
              <select id="pt-purchase" class="field-input" formControlName="purchaseProfileId" aria-label="Purchase tax profile">
                <option value="">Select a profile…</option>
                @for (p of options(group().controls.purchaseProfileId.value); track p.id) { <option [value]="p.id">{{ p.name }}</option> }
              </select>
              @if (group().controls.purchaseProfileId.errors?.['server']; as msg) { <div class="field-error">{{ msg }}</div> }
            }
          </div>
        </div>
      }

      <div class="grid-2">
        <div>
          <label class="field-label" for="pt-hsn">HSN / SAC code</label>
          <input id="pt-hsn" type="text" inputmode="numeric" maxlength="8" class="field-input" formControlName="hsnCode" [placeholder]="hsnPlaceholder()" data-testid="hsn-input" />
          @if (group().controls.hsnCode.errors?.['server']; as msg) { <div class="field-error" data-testid="hsn-error">{{ msg }}</div> }
          @else if (group().controls.hsnCode.errors?.['hsn']) { <div class="field-error" data-testid="hsn-error">{{ hsnMessage }}</div> }
          @else { <div class="field-hint">4, 6 or 8 digits. Leave empty to use the subcategory's code.</div> }
          @if (hsnWarning(); as w) { <div class="warn-box" data-testid="hsn-warning">{{ w }}</div> }
        </div>
        <div>
          <label class="field-label" for="pt-basis">Selling price basis</label>
          <select id="pt-basis" class="field-input" formControlName="priceBasis" data-testid="price-basis">
            <option value="">Company default</option>
            <option value="true">Prices include tax</option>
            <option value="false">Prices exclude tax</option>
          </select>
          @if (basisChanged()) {
            <div class="warn-box" data-testid="basis-warning">Existing selling prices are not converted. Check the prices of this product after changing the basis.</div>
          }
        </div>
      </div>
    </section>
  `,
})
export class ProductTaxSection implements OnInit {
  private readonly service = inject(TaxSetupService);
  private readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);

  readonly group = input.required<ProductTaxGroup>();
  readonly categoryId = input('');
  readonly subcategoryId = input('');
  /** Edit mode: changing the price basis warns (T16). */
  readonly isEdit = input(false);
  /** Price basis the product was loaded with ('' company default, 'true', 'false'); null while loading or for a new product. */
  readonly initialBasis = input<string | null>(null);

  readonly hsnMessage = HSN_MESSAGE;
  readonly resolution = signal<TaxResolution | null>(null);
  readonly profiles = signal<TaxProfile[]>([]);
  readonly canViewTax = computed(() => this.auth.hasPermission('Tax.View'));
  private readonly formState = signal<ResolveQuery>({});
  private readonly currentBasis = signal('');
  private readonly resolve$ = new Subject<ResolveQuery>();
  private profilesCountry = '';

  readonly sourceText = computed(() => {
    const r = this.resolution();
    if (!r?.resolved || !r.source) return '';
    return r.source === 'Company' ? 'From company default' : `From ${r.source.toLowerCase()} ${r.sourceName ?? ''}`.trim();
  });

  readonly hsnPlaceholder = computed(() => {
    const r = this.resolution();
    const inherited = r?.hsnSource === 'Subcategory' ? r.hsnCode : null;
    return inherited ? `Inherited: ${inherited}` : 'e.g. 2202';
  });

  readonly hsnWarning = computed(() => {
    const r = this.resolution();
    return r?.hsnWarning ? (r.hsnWarningMessage ?? 'A taxable GST product should have an HSN/SAC code.') : null;
  });

  readonly basisChanged = computed(() => {
    const initial = this.initialBasis();
    return this.isEdit() && initial !== null && initial !== this.currentBasis();
  });

  constructor() {
    this.resolve$
      .pipe(
        switchMap((q) => this.service.resolve(q).pipe(catchError(() => of(null)))),
        takeUntilDestroyed(),
      )
      .subscribe((r) => {
        this.resolution.set(r);
        if (r?.countryId) this.loadProfiles(r.countryId);
      });

    effect(() => {
      const q = { ...this.formState(), categoryId: this.categoryId(), subcategoryId: this.subcategoryId() };
      this.resolve$.next(q);
    });
  }

  ngOnInit(): void {
    this.captureState();
    this.group().valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.captureState());
  }

  /** Profiles offered in the selects: active ones, plus the current selection so an inactive profile never blanks it. */
  options(currentId: string): TaxProfile[] {
    return this.profiles().filter((p) => p.isActive || p.id === currentId);
  }

  private captureState(): void {
    const v = this.group().getRawValue();
    this.currentBasis.set(v.priceBasis);
    const override = v.overrideEnabled && !!v.salesProfileId;
    this.formState.set({
      // An invalid HSN is not previewed; the field shows its own error.
      hsnCode: isValidHsn(v.hsnCode) ? hsnOrNull(v.hsnCode) : null,
      salesProfileId: override ? v.salesProfileId : null,
      purchaseProfileId: override && !v.purchaseSameAsSales ? v.purchaseProfileId || null : null,
    });
  }

  private loadProfiles(countryId: string): void {
    if (this.profilesCountry === countryId) return;
    this.profilesCountry = countryId;
    this.service.listProfiles(countryId).subscribe({ next: (list) => this.profiles.set(list), error: () => (this.profilesCountry = '') });
  }
}

