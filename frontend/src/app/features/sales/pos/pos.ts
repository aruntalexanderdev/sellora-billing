import { HttpErrorResponse } from '@angular/common/http';
import { IdempotentSubmission } from '../../../core/idempotency';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormGroup, FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { LocationSelect } from '../../../shared/locations/location-select';
import { isEligibleLocation } from '../../../shared/locations/locations.models';
import { StockLocationsService } from '../../../shared/locations/stock-locations.service';
import { splitLocationError } from '../../../shared/locations/location-errors';
import { ConfirmDialogService } from '../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ContextService } from '../../../core/context/context.service';
import { problemMessage } from '../../../core/http/api-errors';
import { UiBadge } from '../../../shared/ui/badge/badge';
import { UiButton } from '../../../shared/ui/button/button';
import { BASE_CURRENCY_DECIMALS, discountExceedsGross, discountFromPercent, discountWithinGross, hasTooManyDecimals, isValidPercent, roundMoney } from '../../../shared/money/money';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { SalesService } from '../sales.service';
import { CustomersService } from '../customers/customers.service';
import type { CustomerItem } from '../customers/customers.models';
import type { SaleCatalogProduct, SaleCatalogUnit } from '../sales.models';
import { defaultSaleUnit, isPriced, needsPriceOverride } from './pos-units';
import { convert, formatQuantity } from '../../../shared/units/unit-conversion';
import { quantityStep, quantityValidators } from '../../../shared/forms/quantity-input';
import { AuthService } from '../../../core/auth/auth.service';
import { LineTaxBreakdown } from '../../../shared/tax/line-tax-breakdown/line-tax-breakdown';
import { billDiscountFields, type BillDiscountMode } from '../../../shared/tax/bill-discount';
import { hsnLineMessage, problemMessagesLabelled } from '../../../shared/tax/hsn';
import { taxByComponent, placeOfSupplyLabel } from '../../../shared/tax/tax-breakdown';
import { TaxPreviewService, planKey, previewStates, type PreviewPlan, type PreviewState } from '../../../shared/tax/tax-preview.service';
import type { TaxPreviewLine, TaxRowView } from '../../../shared/tax/tax-preview.models';
import { applySaleProblem, buildSalePreview, problemField, totalChangedMessage } from './pos-tax';
import type { ApiProblem } from '../../../core/auth/auth.models';
import type { PaymentMethod } from '../sales.models';
import { CashBankService, defaultAccountId } from '../../accounts/cash-bank/cash-bank.service';
import type { AccountOption } from '../../accounts/cash-bank/cash-bank.models';

import type { HasUnsavedChanges } from '../../../core/unsaved-changes.guard';

type LineGroup = FormGroup<{
  productId: FormControl<string>;
  productUnitId: FormControl<string>;
  qty: FormControl<number>;
  unitPrice: FormControl<number>;
  discount: FormControl<number>;
  /** Optional % entry (0-100); only converts to `discount`, never sent. */
  discountPercent: FormControl<number | null>;
}>;

interface LinePreview {
  baseQty: number;
  baseUnitLabel: string;
  availableOnHand: number;
  exceedsStock: boolean;
}

const EMPTY_PREVIEW: LinePreview = {
  baseQty: 0,
  baseUnitLabel: '',
  availableOnHand: 0,
  exceedsStock: false,
};

/** What the cart shows for one line: the server figures, or a tax-less estimate while they are not available. */
interface LineView {
  taxable: number;
  tax: number;
  lineTotal: number;
  /** Combined percent of the line's percent rows (null when not known or per-unit based). */
  ratePercent: number | null;
  hsnCode: string | null;
  priceIncludesTax: boolean;
  rows: TaxRowView[];
  profileName: string | null;
  estimated: boolean;
}

@Component({
  selector: 'app-pos',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, UiBadge, LocationSelect, LineTaxBreakdown],
  templateUrl: './pos.html',
  styleUrl: './pos.scss',
})
export class Pos implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  // One key per sale, so a double-tap on Complete can't sell the stock twice.
  private readonly submission = new IdempotentSubmission();
  private readonly salesService = inject(SalesService);
  private readonly customersService = inject(CustomersService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly stockLocations = inject(StockLocationsService);
  private readonly auth = inject(AuthService);
  private readonly context = inject(ContextService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly taxPreview = inject(TaxPreviewService);

  /** Only holders of Sales.PriceOverride may change a price or discount; everyone else charges the list price. */
  readonly canOverridePrice = computed(() => this.auth.hasPermission('Sales.PriceOverride'));
  readonly formatQuantity = formatQuantity;
  readonly quantityStep = quantityStep;

  private readonly cashBank = inject(CashBankService);
  private readonly allAccountOptions = signal<AccountOption[]>([]);
  /** Accounts usable for this sale: company-level ones plus those of the working branch. */
  readonly accountOptions = computed(() => {
    const branchId = this.context.workingBranch()?.id ?? null;
    return this.allAccountOptions().filter((a) => !a.branchId || a.branchId === branchId);
  });
  /** Selling location (null = server default: the working branch's store). Bound to the picker, which is hidden unless O5 holds. */
  readonly locationId = signal<string | null>(null);
  readonly locationError = signal<string | null>(null);
  private committedLocation: string | null = null;
  private catalogSeq = 0;
  readonly paymentMethods: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'];
  readonly customers = signal<CustomerItem[]>([]);
  readonly catalog = signal<SaleCatalogProduct[]>([]);
  readonly productQuery = signal('');

  readonly searchResults = computed<SaleCatalogProduct[]>(() => {
    const q = this.productQuery().trim().toLowerCase();
    if (!q) return [];
    return this.catalog()
      .filter((p) => p.name.toLowerCase().includes(q) || p.sku.toLowerCase().includes(q) || p.units.some((u) => u.barcode?.toLowerCase() === q))
      .slice(0, 8);
  });

  readonly form = this.fb.nonNullable.group({
    customerId: ['', Validators.required],
    invoiceDate: [today()],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
    accountId: [''],
    paidAmount: [0, [Validators.required, Validators.min(0)]],
    // ---- TP-4: whole-bill discount (percent or amount; needs Sales.BillDiscount) and charges
    billDiscountPercent: [null as number | null, [Validators.min(0), Validators.max(100)]],
    billDiscountAmount: [null as number | null, [Validators.min(0)]],
    billCharges: [0, [Validators.min(0)]],
    lines: this.fb.array<LineGroup>([]),
  });

  /** Only holders of Sales.BillDiscount may give a whole-bill discount; the input is hidden for everyone else. */
  readonly canBillDiscount = computed(() => this.auth.hasPermission('Sales.BillDiscount'));
  /** The bill discount is a percent or an amount, never both: only the active mode's control is shown and sent. */
  readonly billDiscountMode = signal<BillDiscountMode>('percent');

  setBillDiscountMode(mode: BillDiscountMode): void {
    if (mode === this.billDiscountMode()) return;
    this.billDiscountMode.set(mode);
    const other = mode === 'percent' ? this.form.controls.billDiscountAmount : this.form.controls.billDiscountPercent;
    other.setValue(null);
    other.setErrors(null);
  }

  get lines(): FormArray<LineGroup> {
    return this.form.controls.lines;
  }

  private readonly linesValue = toSignal(this.lines.valueChanges, { initialValue: this.lines.value });
  private readonly formValue = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  private readonly paidAmountValue = toSignal(this.form.controls.paidAmount.valueChanges, {
    initialValue: this.form.controls.paidAmount.value,
  });

  readonly previews = computed<LinePreview[]>(() => {
    const catalog = this.catalog();
    const rows = this.linesValue();

    const baseQtys = rows.map((row) => {
      const product = row?.productId ? catalog.find((p) => p.productId === row.productId) : undefined;
      const unit = product?.units.find((u) => u.productUnitId === row?.productUnitId);
      // Preview only; the server computes and validates the stored base quantity.
      return product && unit && row?.qty ? convert(row.qty, unit.conversionFactorToBase, 1) : 0;
    });

    const requestedByProduct = new Map<string, number>();
    rows.forEach((row, i) => {
      const productId = row?.productId;
      if (!productId) return;
      requestedByProduct.set(productId, (requestedByProduct.get(productId) ?? 0) + baseQtys[i]);
    });

    return rows.map((row, i) => {
      const productId = row?.productId;
      const product = productId ? catalog.find((p) => p.productId === productId) : undefined;
      const unit = product?.units.find((u) => u.productUnitId === row?.productUnitId);
      if (!product || !unit || !row?.qty || !productId) return EMPTY_PREVIEW;

      // Quantity facts only; tax and totals come from the server breakdown (`/api/v1/tax/preview`).
      const totalRequested = requestedByProduct.get(productId) ?? 0;

      return {
        baseQty: baseQtys[i],
        baseUnitLabel: product.baseUnitLabel,
        availableOnHand: product.quantityOnHand,
        exceedsStock: totalRequested > product.quantityOnHand,
      };
    });
  });

  readonly hasStockError = computed(() => this.previews().some((p) => p.exceedsStock));

  /** Per-line blocking problem (unpriced unit, or a price/discount change the user may not make); null when fine. */
  readonly lineErrors = computed<(string | null)[]>(() => {
    const catalog = this.catalog();
    const canOverride = this.canOverridePrice();
    return this.linesValue().map((row) => {
      const unit = catalog.find((p) => p.productId === row?.productId)?.units.find((u) => u.productUnitId === row?.productUnitId);
      if (!unit) return null;
      if (!isPriced(unit)) return `${unit.displayName} has no price — set it in Pricing.`;
      if (hasTooManyDecimals(row.discount)) return `Discount can have at most ${BASE_CURRENCY_DECIMALS} decimal places.`;
      if (discountExceedsGross(row.qty, row.unitPrice, row.discount)) return 'Discount cannot be more than quantity × unit price.';
      if (row.discountPercent != null && !isValidPercent(row.discountPercent)) return 'Discount % must be between 0 and 100.';
      if (!canOverride && needsPriceOverride(row.unitPrice, unit.currentPrice, row.discount)) {
        return 'You are not allowed to change the price or give a discount.';
      }
      return null;
    });
  });

  // ---- Live tax breakdown: debounced, newest request wins (switchMap cancels the one in flight) ----
  /** Bumped to re-request an unchanged preview (after "the total is now X" / "rate changed"). */
  private readonly previewNonce = signal(0);

  readonly previewPlan = computed<(PreviewPlan & { nonce: number }) | null>(() => {
    const v = this.formValue();
    const plan = buildSalePreview({
      customerId: v.customerId ?? '',
      invoiceDate: v.invoiceDate ?? '',
      canBillDiscount: this.canBillDiscount(),
      billDiscountMode: this.billDiscountMode(),
      billDiscountPercent: v.billDiscountPercent ?? null,
      billDiscountAmount: v.billDiscountAmount ?? null,
      billCharges: v.billCharges ?? 0,
      lines: (v.lines ?? []).map((l) => ({
        productId: l.productId ?? '', productUnitId: l.productUnitId ?? '', qty: l.qty ?? null, unitPrice: l.unitPrice ?? null, discount: l.discount ?? 0,
      })),
    });
    return plan ? { ...plan, nonce: this.previewNonce() } : null;
  });

  private readonly previewState = toSignal(
    previewStates(toObservable(this.previewPlan), (request) => this.taxPreview.preview(request)),
    { initialValue: { kind: 'idle' } as PreviewState },
  );

  /** The latest server breakdown (kept while a newer one is loading; used for the totals only). */
  readonly preview = computed(() => {
    const s = this.previewState();
    return s.kind === 'ok' ? s.preview : null;
  });
  /** The cart changed since the shown breakdown was requested (or none was requested yet). */
  readonly previewPending = computed(() => {
    const plan = this.previewPlan();
    if (!plan) return false;
    const s = this.previewState();
    return planKey(plan) !== (s.kind === 'idle' ? '' : planKey(s.plan));
  });
  private readonly previewProblem = computed(() => {
    const s = this.previewState();
    return s.kind === 'error' && !this.previewPending() ? s.problem : null;
  });
  /** Why the current cart could not be calculated (e.g. a stock shortage or a bill discount over the cap); null when fine. */
  readonly previewError = computed(() => {
    const s = this.previewState();
    if (s.kind !== 'error' || this.previewPending()) return null;
    const fields = problemMessagesLabelled(s.problem?.errors, s.plan.rows);
    return fields.length > 0 ? fields.join(' ') : problemMessage(s.problem, 'Tax could not be calculated.');
  });
  /** Server message of a bill field from the preview (cap exceeded, both given...). */
  readonly previewBillError = computed(
    () => problemField(this.previewProblem(), 'billDiscountPercent') ?? problemField(this.previewProblem(), 'billDiscountAmount'),
  );

  /** The bill discount message: from the last save attempt, else from the preview (cap exceeded). */
  billDiscountError(): string | null {
    const c = this.billDiscountMode() === 'percent' ? this.form.controls.billDiscountPercent : this.form.controls.billDiscountAmount;
    const own = c.errors?.['server'] as string | undefined;
    if (own) return own;
    if (c.errors?.['max']) return 'Enter a percent between 0 and 100.';
    if (c.errors?.['min']) return 'Enter an amount of zero or more.';
    return this.previewBillError();
  }

  /** Soft findings of the preview (e.g. a missing HSN), prefixed with the cart row number. */
  readonly previewWarnings = computed<string[]>(() => {
    const s = this.previewState();
    if (s.kind !== 'ok') return [];
    return s.preview.warnings.map((w) => saleWarningLabel(w.field, w.message, s.plan.rows));
  });

  private readonly previewRows = computed(() => {
    const s = this.previewState();
    const map = new Map<number, TaxPreviewLine>();
    // While a newer breakdown is pending, the shown one was planned for the old rows (a deleted row would shift the
    // figures onto the wrong lines), so lines fall back to a tax-less estimate until it arrives (TP-3 review).
    if (s.kind === 'ok' && !this.previewPending()) for (const l of s.preview.lines) map.set(s.plan.rows[l.index], l);
    return map;
  });

  readonly precision = computed(() => this.preview()?.precision ?? BASE_CURRENCY_DECIMALS);
  readonly money = (n: number) => '₹' + n.toFixed(this.precision());

  readonly lineViews = computed<LineView[]>(() => {
    const server = this.previewRows();
    const catalog = this.catalog();
    const places = this.precision();
    return this.linesValue().map((row, i) => {
      const line = server.get(i);
      if (line) {
        const percentRows = line.rows.filter((r) => r.method === 'Percent');
        return {
          taxable: line.taxable, tax: line.taxTotal, lineTotal: line.lineTotal, ratePercent: percentRows.length === line.rows.length ? line.taxRatePercent : null,
          hsnCode: line.hsnCode, priceIncludesTax: line.priceIncludesTax, rows: line.rows, profileName: line.profileName, estimated: false,
        };
      }
      const product = row?.productId ? catalog.find((p) => p.productId === row.productId) : undefined;
      const gross = roundMoney((row?.qty ?? 0) * (row?.unitPrice ?? 0) - (row?.discount ?? 0), places);
      return {
        taxable: gross, tax: 0, lineTotal: gross, ratePercent: null, hsnCode: product?.hsnCode ?? null, priceIncludesTax: !!product?.priceIncludesTax,
        rows: [], profileName: null, estimated: true,
      };
    });
  });

  readonly totals = computed(() => {
    const p = this.preview();
    if (p) {
      return {
        subtotal: p.subtotal, discount: p.discountTotal, billDiscount: p.billDiscountAmount, charges: p.chargesTotal, taxable: p.taxableTotal, tax: p.taxTotal,
        grandTotal: p.grandTotal, byComponent: taxByComponent(p.lines), estimated: false,
      };
    }
    const rows = this.linesValue();
    let subtotal = 0,
      discount = 0;
    rows.forEach((row) => {
      subtotal += roundMoney((row?.qty ?? 0) * (row?.unitPrice ?? 0));
      discount += roundMoney(row?.discount ?? 0);
    });
    const charges = this.formValue().billCharges ?? 0;
    const grandTotal = roundMoney(subtotal - discount + charges);
    return { subtotal: roundMoney(subtotal), discount: roundMoney(discount), billDiscount: 0, charges, taxable: grandTotal, tax: 0, grandTotal, byComponent: [], estimated: true };
  });

  readonly placeLabel = computed(() => {
    const p = this.preview();
    return p ? placeOfSupplyLabel(p.placeOfSupply, p.regime) : null;
  });

  /** True when the bill carries a price-inclusive line (the subtotal then includes tax). */
  readonly anyInclusive = computed(() => this.lineViews().some((l) => l.priceIncludesTax));

  /** The sale figures are final only once the preview of exactly this cart is in. */
  readonly completeSaleDisabledReason = computed(() => {
    if (this.linesValue().length === 0) return 'Add at least one item to the cart first.';
    if (this.lineErrors().some((e) => !!e)) return 'Fix the highlighted items — a unit has no price or a price is not allowed.';
    if (this.hasStockError()) return 'Reduce quantity — not enough stock for one or more items.';
    if (this.previewPending()) return 'Calculating tax…';
    if (this.previewError()) return `Tax could not be calculated: ${this.previewError()}`;
    if (!this.preview() || this.previewPlan() === null) return 'Waiting for the tax calculation — check the customer and quantities.';
    return null;
  });

  readonly dueAmount = computed(() => Math.max(0, roundMoney(this.totals().grandTotal - (this.paidAmountValue() ?? 0), this.precision())));
  readonly changeAmount = computed(() => Math.max(0, roundMoney((this.paidAmountValue() ?? 0) - this.totals().grandTotal, this.precision())));

  private readonly expandedRows = signal<ReadonlySet<LineGroup>>(new Set());
  isExpanded(row: LineGroup): boolean {
    return this.expandedRows().has(row);
  }
  toggleTax(row: LineGroup): void {
    this.expandedRows.update((set) => {
      const next = new Set(set);
      if (!next.delete(row)) next.add(row);
      return next;
    });
  }

  /** Server message(s) stored on the controls of a cart row (qty, price, discount, unit), shown under the row. */
  rowServerError(row: LineGroup): string | null {
    const c = row.controls;
    const messages = [c.productId, c.productUnitId, c.qty, c.unitPrice, c.discount]
      .map((x) => x.errors?.['server'] as string | undefined)
      .filter((m): m is string => !!m);
    return messages.length > 0 ? messages.join(' ') : null;
  }

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.lines.length > 0;
  }

  ngOnInit(): void {
    this.cashBank.options().subscribe({
      next: (o) => {
        this.allAccountOptions.set(o);
        this.form.patchValue({ accountId: defaultAccountId(this.accountOptions(), this.form.controls.paymentMethod.value) });
        this.form.controls.accountId.markAsPristine();
      },
    });
    this.form.controls.paymentMethod.valueChanges.subscribe((m) =>
      this.form.patchValue({ accountId: defaultAccountId(this.accountOptions(), m) }),
    );
    this.customersService.list().subscribe({
      next: (res) => {
        const active = res.items.filter((c) => c.isActive);
        this.customers.set(active);
        const walkIn = active.find((c) => c.isWalkIn);
        if (walkIn) this.form.controls.customerId.setValue(walkIn.id);
      },
    });
    this.preselectLocation();
  }

  /**
   * `?locationId=` (from the van workspace) preselects the selling location when it is an eligible, visible sale location;
   * anything else is ignored silently and the server default stays. The catalog loads once the choice is known.
   */
  private preselectLocation(): void {
    const wanted = this.route.snapshot.queryParamMap.get('locationId');
    if (!wanted || !this.auth.hasPermission('StockLocations.View')) {
      this.loadCatalog();
      return;
    }
    this.stockLocations.list().subscribe({
      next: (all) => {
        if (all.some((l) => l.id === wanted && l.isActive && isEligibleLocation(l, 'sale'))) {
          this.committedLocation = wanted;
          this.locationId.set(wanted);
        }
        this.loadCatalog();
      },
      error: () => this.loadCatalog(),
    });
  }

  /** Loads the catalog at the selling location; a stale response (location changed meanwhile) is dropped. */
  private loadCatalog(): void {
    const seq = ++this.catalogSeq;
    this.salesService.catalog(undefined, this.committedLocation).subscribe({
      next: (res) => {
        if (seq === this.catalogSeq) this.catalog.set(res);
      },
    });
  }

  /**
   * Picker change. Availability and prices are per location, so with items in the cart the user must confirm; the cart is then
   * cleared. Declining puts the picker back on the committed location.
   */
  async onLocationChange(next: string | null): Promise<void> {
    this.locationId.set(next);
    if (next === this.committedLocation) return;
    this.locationError.set(null);
    if (this.lines.length > 0) {
      const ok = await this.confirmDialog.ask({
        title: 'Change selling location?',
        body: 'Stock availability is per location, so the items in the cart will be cleared.',
        confirmLabel: 'Change and clear cart',
        destructive: true,
      });
      if (!ok) {
        this.locationId.set(this.committedLocation);
        return;
      }
      this.lines.clear();
    }
    this.committedLocation = next;
    // The old list belongs to the previous location: nothing may be added from it while the new one loads.
    this.catalog.set([]);
    this.loadCatalog();
  }

  addToCart(product: SaleCatalogProduct): void {
    const unit = defaultSaleUnit(product.units);
    if (!unit) {
      this.toast.show(`${product.name} has no priced unit — set a price in Pricing.`);
      return;
    }
    this.addLine(product, unit);
    this.productQuery.set('');
  }

  /** Adds a cart line in the given unit, or bumps the quantity of an existing line in that same unit. */
  addLine(product: SaleCatalogProduct, unit: SaleCatalogUnit): void {
    if (!isPriced(unit)) {
      this.toast.show(`${product.name} ${unit.displayName} has no price — set it in Pricing.`);
      return;
    }
    const existing = this.lines.controls.find(
      (r) => r.controls.productId.value === product.productId && r.controls.productUnitId.value === unit.productUnitId,
    );
    if (existing) {
      existing.controls.qty.setValue(Math.round((existing.controls.qty.value + 1) * 1000) / 1000);
      return;
    }
    this.lines.push(
      this.fb.group(
        {
          productId: this.fb.nonNullable.control(product.productId, Validators.required),
          productUnitId: this.fb.nonNullable.control(unit.productUnitId, Validators.required),
          qty: this.fb.nonNullable.control(1, quantityValidators(unit.decimalPlaces)),
          unitPrice: this.fb.nonNullable.control(unit.currentPrice ?? 0, [Validators.required, Validators.min(0)]),
          discount: this.fb.nonNullable.control(0, [Validators.min(0)]),
          discountPercent: new FormControl<number | null>(null, [Validators.min(0), Validators.max(100)]),
        },
        { validators: discountWithinGross },
      ) as LineGroup,
    );
  }

  /** Enter in the search box: an exact barcode lookup resolves product and unit (weighed codes are not supported). */
  scan(event?: Event): void {
    event?.preventDefault();
    const code = this.productQuery().trim();
    if (!code) return;
    this.salesService.lookupBarcode(code, this.committedLocation).subscribe({
      next: (match) => {
        this.catalog.update((c) =>
          c.some((p) => p.productId === match.product.productId)
            ? c.map((p) => (p.productId === match.product.productId ? match.product : p))
            : [...c, match.product],
        );
        const unit = match.product.units.find((u) => u.productUnitId === match.productUnitId);
        if (!unit) {
          this.toast.show(`No sellable item found for barcode ${code}.`);
          return;
        }
        this.addLine(match.product, unit);
        this.productQuery.set('');
      },
      error: (err: HttpErrorResponse) =>
        this.toast.show(err.status === 404 ? `No sellable item found for barcode ${code}.` : 'Could not look up that barcode.'),
    });
  }

  onUnitChange(row: LineGroup): void {
    const unit = this.selectedUnit(row);
    // A new unit means a new list price: any earlier override is cleared.
    row.controls.unitPrice.setValue(unit?.currentPrice ?? 0);
    row.controls.qty.setValidators(quantityValidators(unit?.decimalPlaces));
    row.controls.qty.updateValueAndValidity();
  }

  /** Quantity `step` of a line, from its selected unit's decimal places. */
  stepFor(row: LineGroup): string {
    return quantityStep(this.selectedUnit(row)?.decimalPlaces);
  }

  /** List price of a line's selected unit (null when unpriced). */
  listPriceFor(row: LineGroup): number | null {
    return this.selectedUnit(row)?.currentPrice ?? null;
  }

  /** Free item: unit price 0 (needs Sales.PriceOverride like any price change). */
  isFree(row: LineGroup): boolean {
    return row.controls.unitPrice.value === 0;
  }

  /** The % box was edited: convert to the discount amount (rounded, never above the gross). */
  onPercentInput(row: LineGroup): void {
    const pct = row.controls.discountPercent.value;
    if (pct == null) return;
    row.controls.discount.setValue(discountFromPercent(row.controls.qty.value, row.controls.unitPrice.value, pct));
  }

  /** Qty or price changed while a % is entered: keep the amount in step with the percentage. */
  onGrossInput(row: LineGroup): void {
    this.onPercentInput(row);
  }

  /** The amount was typed directly: it replaces any % entry. */
  onDiscountAmountInput(row: LineGroup): void {
    row.controls.discountPercent.setValue(null);
  }

  isOverride(row: LineGroup): boolean {
    return needsPriceOverride(row.controls.unitPrice.value, this.listPriceFor(row), row.controls.discount.value);
  }

  unitsFor(productId: string): SaleCatalogUnit[] {
    return this.catalog().find((p) => p.productId === productId)?.units ?? [];
  }

  private selectedUnit(row: LineGroup): SaleCatalogUnit | undefined {
    return this.unitsFor(row.controls.productId.value).find((u) => u.productUnitId === row.controls.productUnitId.value);
  }

  productName(productId: string): string {
    return this.catalog().find((p) => p.productId === productId)?.name ?? '';
  }

  /** TP-5a: why this product has no real tax rate today (no sales profile / valid version), or null. */
  taxWarningOf(productId: string): string | null {
    return this.catalog().find((p) => p.productId === productId)?.taxWarning ?? null;
  }

  private unitPriceOf(productId: string, productUnitId: string): number | null {
    return this.unitsFor(productId).find((u) => u.productUnitId === productUnitId)?.currentPrice ?? null;
  }

  /** After a "price is now X" rejection: reload the carted products and move un-overridden lines to the new list price. */
  private refreshPrices(): void {
    const skus = new Set(
      this.lines.controls.map((r) => this.catalog().find((p) => p.productId === r.controls.productId.value)?.sku).filter((s): s is string => !!s),
    );
    for (const sku of skus) {
      this.salesService.catalog(sku, this.committedLocation).subscribe({
        next: (fresh) => {
          for (const product of fresh.filter((p) => p.sku === sku)) {
            for (const row of this.lines.controls.filter((r) => r.controls.productId.value === product.productId)) {
              const oldList = this.unitPriceOf(product.productId, row.controls.productUnitId.value);
              const newList = product.units.find((u) => u.productUnitId === row.controls.productUnitId.value)?.currentPrice;
              if (newList != null && !needsPriceOverride(row.controls.unitPrice.value, oldList, 0)) row.controls.unitPrice.setValue(newList);
            }
            this.catalog.update((c) => c.map((p) => (p.productId === product.productId ? product : p)));
          }
        },
      });
    }
  }

  removeLine(index: number): void {
    this.lines.removeAt(index);
  }

  submit(): void {
    this.serverError.set(null);

    if (this.lines.length === 0) {
      this.serverError.set('Add at least one item.');
      return;
    }
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    if (this.lineErrors().some((e) => !!e)) {
      this.serverError.set(this.lineErrors().find((e) => !!e) ?? null);
      return;
    }
    if (this.hasStockError()) {
      this.serverError.set('Reduce quantity — not enough stock for one or more items.');
      return;
    }
    // Cash is only taken on a total the server has calculated for exactly this cart.
    const blocked = this.completeSaleDisabledReason();
    const preview = this.preview();
    if (blocked || !preview) {
      this.serverError.set(blocked ?? 'Waiting for the tax calculation.');
      return;
    }

    this.saving.set(true);
    const v = this.form.getRawValue();
    const body = {
        customerId: v.customerId,
        invoiceDate: v.invoiceDate,
        paymentMethod: v.paymentMethod,
        paidAmount: v.paidAmount,
        accountId: v.accountId || null,
        stockLocationId: this.committedLocation,
        ...(this.canBillDiscount()
          ? billDiscountFields(this.billDiscountMode(), v.billDiscountPercent, v.billDiscountAmount)
          : { billDiscountPercent: null, billDiscountAmount: null }),
        billCharges: v.billCharges ?? 0,
        expectedGrandTotal: preview.grandTotal,
        lines: v.lines.map((l) => ({
          productId: l.productId,
          productUnitId: l.productUnitId,
          qty: l.qty,
          unitPrice: l.unitPrice,
          discount: l.discount,
          overridePrice: this.canOverridePrice() && needsPriceOverride(l.unitPrice, this.unitPriceOf(l.productId, l.productUnitId), l.discount),
        })),
      };
    this.salesService
      .create(body, this.submission.keyFor(body))
      .subscribe({
        next: (invoice) => {
          this.saving.set(false);
          this.saved = true;
          this.toast.show(`${invoice.invoiceNo} completed · stock updated.`);
          const warnings = (invoice.warnings ?? []).map((w) => saleWarningLabel(w.field, w.message));
          // The detail screen shows the warnings once (GET returns none).
          this.router.navigate(['/sales', invoice.id], warnings.length > 0 ? { state: { warnings } } : undefined);
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          const { locationError, rest } = splitLocationError(err.error as ApiProblem | undefined);
          if (locationError) this.locationError.set(locationError);
          const totalChanged = totalChangedMessage(rest);
          if (totalChanged) {
            // The total moved while the cashier looked at it: re-calculate and say what it is now. Nothing was sold.
            this.previewNonce.update((n) => n + 1);
            this.serverError.set(`${totalChanged} Check the amount, then complete the sale again.`);
            return;
          }
          const hasFieldErrors = Object.keys(rest?.errors ?? {}).length > 0;
          let message: string | null;
          if (hasFieldErrors) {
            message = applySaleProblem(this.form, rest) ?? locationError ?? 'Fix the highlighted fields and try again.';
          } else if (err.status === 403 && (body.billDiscountPercent != null || body.billDiscountAmount != null)) {
            message = null;
            const control = body.billDiscountPercent != null ? this.form.controls.billDiscountPercent : this.form.controls.billDiscountAmount;
            control.setErrors({ server: problemMessage(rest, 'You are not allowed to give a bill discount.') });
            control.markAsTouched();
          } else {
            message = locationError ?? problemMessage(rest, 'Something went wrong.');
          }
          this.serverError.set(message);
          const allText = Object.values(rest?.errors ?? {}).flat().join(' ');
          if (/price of .* is now|tax rate changed/i.test(allText)) {
            // The refresh moves the price (clearing the field error), so the new price must stay visible in the banner.
            const moved = Object.values(rest?.errors ?? {}).flat().filter((m) => /is now|tax rate changed/i.test(m)).join(' ');
            this.serverError.set(message && message !== 'Fix the highlighted fields and try again.' ? message : moved);
            this.refreshPrices();
            this.previewNonce.update((n) => n + 1);
          }
        },
      });
  }
}

/** Warning text for a `lines[i].hsnCode` field of the create response, prefixed with the item number. */
export const saleWarningLabel = hsnLineMessage;

function today(): string {
  return new Date().toISOString().slice(0, 10);
}
