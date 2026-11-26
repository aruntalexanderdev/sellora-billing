import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { merge } from 'rxjs';
import { FormArray, FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { LocationSelect } from '../../../shared/locations/location-select';
import { splitLocationError } from '../../../shared/locations/location-errors';
import { ContextService } from '../../../core/context/context.service';
import { problemMessage } from '../../../core/http/api-errors';
import { UiButton } from '../../../shared/ui/button/button';
import { UiBadge } from '../../../shared/ui/badge/badge';
import { LineTaxBreakdown } from '../../../shared/tax/line-tax-breakdown/line-tax-breakdown';
import { problemMessagesLabelled } from '../../../shared/tax/hsn';
import { taxByComponent, placeOfSupplyLabel } from '../../../shared/tax/tax-breakdown';
import { TaxPreviewService, planKey, previewStates, type PreviewState } from '../../../shared/tax/tax-preview.service';
import type { TaxPreviewLine, TaxRowView } from '../../../shared/tax/tax-preview.models';
import { applyPurchaseProblem, buildPurchasePreview, type BillDiscountMode, billDiscountFields, warningLabel } from '../purchase-tax';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { PurchasesService } from '../purchases.service';
import { SuppliersService } from '../suppliers/suppliers.service';
import { ProductsService } from '../../inventory/products/products.service';
import { ExchangeRatesService } from '../../administration/exchange-rates/exchange-rates.service';
import type { ResolvedExchangeRate } from '../../administration/exchange-rates/exchange-rates.models';
import type { SupplierItem } from '../suppliers/suppliers.models';
import type { ApiProblem } from '../../../core/auth/auth.models';
import type { PaymentMethod, PurchaseProduct, PurchaseProductUnit, PurchaseWarning } from '../purchases.models';
import { defaultPurchaseUnit, purchaseBaseDecimalsError, purchaseUnitOptions } from './purchase-units';
import { convert, formatQuantity } from '../../../shared/units/unit-conversion';
import { quantityStep, quantityValidators } from '../../../shared/forms/quantity-input';
import { CashBankService, defaultAccountId } from '../../accounts/cash-bank/cash-bank.service';
import type { AccountOption } from '../../accounts/cash-bank/cash-bank.models';

import type { HasUnsavedChanges } from '../../../core/unsaved-changes.guard';
import { IdempotentSubmission } from '../../../core/idempotency';

type LineGroup = FormGroup<{
  productId: FormControl<string>;
  productUnitId: FormControl<string>;
  qty: FormControl<number | null>;
  rate: FormControl<number | null>;
  otherCharges: FormControl<number>;
  discount: FormControl<number>;
  supplierBatchNumber: FormControl<string>;
}>;

/** Client-side facts of one row (quantity conversion) plus the gross fallback used until the server breakdown arrives. */
interface LinePreview {
  valid: boolean;
  baseQty: number;
  baseUnitLabel: string;
  baseUnitDecimalPlaces: number;
  /** Server-mirrored base-decimals error for the entered quantity, if any. */
  baseDecimalsError: string | null;
  subtotal: number;
  otherCharges: number;
  discount: number;
}

const EMPTY_PREVIEW: LinePreview = {
  valid: false, baseQty: 0, baseUnitLabel: '', baseUnitDecimalPlaces: 3, baseDecimalsError: null, subtotal: 0, otherCharges: 0, discount: 0,
};

/** What a row shows: the server's figures when the breakdown is current, else a tax-less client estimate. */
interface LineView {
  taxable: number;
  tax: number;
  lineTotal: number;
  unitCost: number;
  hsnCode: string | null;
  priceIncludesTax: boolean;
  rows: TaxRowView[];
  /** True while the figures are the client fallback (no server breakdown yet). */
  estimated: boolean;
  profileName: string | null;
}

const roundTo = (n: number, places: number) => {
  const f = 10 ** places;
  return Math.sign(n) * Math.round(Math.abs(n) * f + Number.EPSILON) / f;
};

@Component({
  selector: 'app-purchase-form',
  standalone: true,
  imports: [LineTaxBreakdown, LocationSelect, ReactiveFormsModule, RouterLink, UiBadge, UiButton],
  templateUrl: './purchase-form.html',
  styleUrl: './purchase-form.scss',
})
export class PurchaseForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  // One key per form, so a double-click or retry can't receive stock twice.
  private readonly submission = new IdempotentSubmission();
  private readonly purchasesService = inject(PurchasesService);
  private readonly suppliersService = inject(SuppliersService);
  private readonly productsService = inject(ProductsService);
  private readonly exchangeRatesService = inject(ExchangeRatesService);
  private readonly cashBank = inject(CashBankService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly context = inject(ContextService);
  private readonly taxPreview = inject(TaxPreviewService);

  readonly paymentMethods: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'];
  private readonly allAccountOptions = signal<AccountOption[]>([]);
  /** Accounts usable for this purchase: company-level ones plus those of the working branch. */
  readonly accountOptions = computed(() => {
    const branchId = this.context.workingBranch()?.id ?? null;
    return this.allAccountOptions().filter((a) => !a.branchId || a.branchId === branchId);
  });
  /** Receiving location (null = server default). The picker shows only for StockLocations.View holders with > 1 eligible location. */
  readonly locationId = signal<string | null>(null);
  readonly locationError = signal<string | null>(null);
  readonly suppliers = signal<SupplierItem[]>([]);
  readonly products = signal<{ id: string; name: string; sku: string }[]>([]);
  private readonly productCache = signal<Map<string, PurchaseProduct>>(new Map());

  readonly form = this.fb.nonNullable.group({
    supplierId: ['', Validators.required],
    purchaseDate: [today(), Validators.required],
    supplierInvoiceNumber: [''],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
    accountId: [''],
    paidAmount: [0, [Validators.required, Validators.min(0)]],
    notes: [''],
    // ---- TP-3: tax basis and bill-level discount / charges
    ratesIncludeTax: [false],
    billDiscountPercent: [null as number | null, [Validators.min(0), Validators.max(100)]],
    billDiscountAmount: [null as number | null, [Validators.min(0)]],
    billCharges: [0, [Validators.min(0)]],
    lines: this.fb.array<LineGroup>([]),
  });

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
  private readonly paidAmountValue = toSignal(this.form.controls.paidAmount.valueChanges, {
    initialValue: this.form.controls.paidAmount.value,
  });

  private readonly formValue = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });

  readonly previews = computed<LinePreview[]>(() => {
    const cache = this.productCache();
    return this.linesValue().map((row) => {
      const product = row?.productId ? cache.get(row.productId) : undefined;
      const unit = product?.units.find((u) => u.id === row?.productUnitId);
      if (!product || !unit || !row?.qty || row.rate == null) return EMPTY_PREVIEW;

      // Quantity facts only; tax and totals come from the server breakdown (`/api/v1/tax/preview`).
      const baseQty = convert(row.qty, unit.conversionFactorToBase, 1);
      const baseUnitDecimalPlaces = product.units.find((u) => u.isBaseUnit)?.decimalPlaces ?? 3;
      const baseDecimalsError = purchaseBaseDecimalsError(row.qty, unit, baseUnitDecimalPlaces, product.baseUnitLabel);
      return {
        valid: true, baseQty, baseUnitLabel: product.baseUnitLabel, baseUnitDecimalPlaces, baseDecimalsError,
        subtotal: row.qty * row.rate, otherCharges: row.otherCharges ?? 0, discount: row.discount ?? 0,
      };
    });
  });

  // ---- Live tax breakdown: debounced, newest request wins (switchMap cancels the one in flight) ----
  readonly previewPlan = computed(() => {
    const v = this.formValue();
    return buildPurchasePreview({
      supplierId: v.supplierId ?? '',
      purchaseDate: v.purchaseDate ?? '',
      ratesIncludeTax: v.ratesIncludeTax ?? false,
      billDiscountMode: this.billDiscountMode(),
      billDiscountPercent: v.billDiscountPercent ?? null,
      billDiscountAmount: v.billDiscountAmount ?? null,
      billCharges: v.billCharges ?? 0,
      lines: (v.lines ?? []).map((l) => ({
        productId: l.productId ?? '', productUnitId: l.productUnitId ?? '', qty: l.qty ?? null, rate: l.rate ?? null,
        otherCharges: l.otherCharges ?? 0, discount: l.discount ?? 0,
      })),
    });
  });

  private readonly previewState = toSignal(
    previewStates(toObservable(this.previewPlan), (request) => this.taxPreview.preview(request)),
    { initialValue: { kind: 'idle' } as PreviewState },
  );

  /** The latest server breakdown (kept while a newer one is loading). */
  readonly preview = computed(() => {
    const s = this.previewState();
    return s.kind === 'ok' ? s.preview : null;
  });
  /** The form changed since the shown breakdown was requested. */
  readonly previewPending = computed(() => {
    const s = this.previewState();
    return planKey(this.previewPlan()) !== (s.kind === 'idle' ? '' : planKey(s.plan));
  });
  readonly previewError = computed(() => {
    const s = this.previewState();
    if (s.kind !== 'error' || this.previewPending()) return null;
    const fields = problemMessagesLabelled(s.problem?.errors, s.plan.rows);
    return fields.length > 0 ? fields.join(' ') : problemMessage(s.problem, 'Tax could not be calculated.');
  });

  private readonly previewRows = computed(() => {
    const s = this.previewState();
    const map = new Map<number, TaxPreviewLine>();
    // While a newer breakdown is pending, the shown one was planned for the old rows (a deleted row would shift the
    // figures onto the wrong lines), so lines fall back to their local estimate until it arrives (TP-3 review).
    if (s.kind === 'ok' && !this.previewPending()) for (const l of s.preview.lines) map.set(s.plan.rows[l.index], l);
    return map;
  });

  readonly precision = computed(() => this.preview()?.precision ?? this.selectedSupplier()?.currencyDecimalPrecision ?? 2);
  readonly money = (n: number) => this.currencySymbol() + n.toFixed(this.precision());

  readonly lineViews = computed<LineView[]>(() => {
    const previews = this.previews();
    const server = this.previewRows();
    return this.linesValue().map((row, i) => {
      const fallback = previews[i];
      const line = server.get(i);
      if (line) {
        return {
          taxable: line.taxable, tax: line.taxTotal, lineTotal: line.lineTotal, hsnCode: line.hsnCode, priceIncludesTax: line.priceIncludesTax,
          unitCost: line.baseQty > 0 ? line.taxable / line.baseQty : 0, rows: line.rows, estimated: false, profileName: line.profileName,
        };
      }
      const taxable = fallback?.valid ? fallback.subtotal + fallback.otherCharges - fallback.discount : 0;
      return {
        taxable, tax: 0, lineTotal: taxable, hsnCode: null, priceIncludesTax: false, rows: [], estimated: true, profileName: null,
        unitCost: fallback?.valid && fallback.baseQty > 0 ? taxable / fallback.baseQty : 0,
      };
    });
  });

  readonly totals = computed(() => {
    const p = this.preview();
    if (p) {
      return {
        subtotal: p.subtotal, otherCharges: p.chargesTotal, discount: p.discountTotal, taxable: p.taxableTotal, tax: p.taxTotal,
        grandTotal: p.grandTotal, byComponent: taxByComponent(p.lines), billDiscount: p.billDiscountAmount, estimated: false,
      };
    }
    let subtotal = 0, otherCharges = 0, discount = 0;
    this.linesValue().forEach((row, i) => {
      const preview = this.previews()[i];
      subtotal += preview?.valid ? preview.subtotal : (row?.qty ?? 0) * (row?.rate ?? 0);
      otherCharges += preview?.valid ? preview.otherCharges : row?.otherCharges ?? 0;
      discount += preview?.valid ? preview.discount : row?.discount ?? 0;
    });
    const taxable = subtotal + otherCharges - discount;
    return { subtotal, otherCharges, discount, taxable, tax: 0, grandTotal: taxable, byComponent: [], billDiscount: 0, estimated: true };
  });

  readonly placeLabel = computed(() => {
    const p = this.preview();
    return p ? placeOfSupplyLabel(p.placeOfSupply, p.regime) : null;
  });

  /** Soft findings of the preview (supplier without state, HSN), with the form row number for `lines[i]` fields. */
  readonly previewWarnings = computed<string[]>(() => {
    const s = this.previewState();
    if (s.kind !== 'ok') return [];
    return s.preview.warnings.map((w) => warningLabel(w.field, w.message, s.plan.rows));
  });
  readonly saveWarnings = signal<string[]>([]);

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

  private readonly supplierIdValue = toSignal(this.form.controls.supplierId.valueChanges, { initialValue: '' });

  // Rates, charges and discounts are entered in the supplier's currency; the backend derives the
  // currency and exchange rate itself on save, so this is only a preview of what will be recorded.
  readonly selectedSupplier = computed(() => this.suppliers().find((s) => s.id === this.supplierIdValue()));
  readonly currencySymbol = computed(() => this.selectedSupplier()?.currencySymbol ?? '₹');
  readonly resolvedRate = signal<ResolvedExchangeRate | null>(null);
  readonly rateError = signal<string | null>(null);

  readonly isForeign = computed(() => {
    const supplier = this.selectedSupplier();
    const rate = this.resolvedRate();
    return !!supplier && !!rate && rate.baseCurrencyCode !== supplier.currencyCode;
  });

  // An estimate: the server converts each part and tax row to the base currency and sums them.
  readonly baseGrandTotal = computed(() => (this.isForeign() ? roundTo(this.totals().grandTotal * (this.resolvedRate()?.rateToBase ?? 1), this.resolvedRate()?.baseCurrencyDecimalPrecision ?? 2) : this.totals().grandTotal));
  readonly baseSymbol = computed(() => this.resolvedRate()?.baseCurrencySymbol ?? this.currencySymbol());

  // The payment is entered in the supplier's currency (its code labels the field), so that is also the
  // payment currency by default. The server converts it and is authoritative; this only previews the
  // same steps: a payment that clears the whole total books the exact base total, otherwise the paid
  // amount is converted at the resolved rate and rounded to the base currency's precision.
  readonly paymentCode = computed(() => this.selectedSupplier()?.currencyCode ?? this.resolvedRate()?.baseCurrencyCode ?? 'INR');
  readonly baseCode = computed(() => this.resolvedRate()?.baseCurrencyCode ?? this.paymentCode());

  private readonly paidInPaymentCurrency = computed(() => {
    const paid = this.paidAmountValue() ?? 0;
    return this.isForeign() ? roundTo(paid, this.selectedSupplier()?.currencyDecimalPrecision ?? 2) : paid;
  });

  readonly dueInPaymentCurrency = computed(() => Math.max(0, this.totals().grandTotal - this.paidInPaymentCurrency()));

  readonly dueInBaseCurrency = computed(() => {
    if (!this.isForeign()) return this.dueInPaymentCurrency();
    const paid = this.paidInPaymentCurrency();
    const paidBase =
      paid >= this.totals().grandTotal
        ? this.baseGrandTotal()
        : roundTo(paid * (this.resolvedRate()?.rateToBase ?? 1), this.resolvedRate()?.baseCurrencyDecimalPrecision ?? 2);
    return Math.max(0, this.baseGrandTotal() - paidBase);
  });

  readonly submitDisabledReason = computed(() =>
    this.linesValue().length === 0 ? 'Add at least one item before receiving stock.' : null,
  );

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
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
    this.suppliersService.list().subscribe({ next: (res) => this.suppliers.set(res.items.filter((s) => s.isActive)) });
    this.productsService.list().subscribe({
      next: (res) => this.products.set(res.items.filter((p) => p.isActive).map((p) => ({ id: p.id, name: p.name, sku: p.sku }))),
    });
    this.addLine();

    merge(this.form.controls.supplierId.valueChanges, this.form.controls.purchaseDate.valueChanges).subscribe(() => this.refreshRate());
  }

  private refreshRate(): void {
    const supplier = this.suppliers().find((s) => s.id === this.form.controls.supplierId.value);
    const date = this.form.controls.purchaseDate.value;
    this.resolvedRate.set(null);
    this.rateError.set(null);
    if (!supplier || !date) return;

    this.exchangeRatesService.resolve(supplier.currencyId, date).subscribe({
      next: (rate) => this.resolvedRate.set(rate),
      error: (err: HttpErrorResponse) => {
        const problem = err.error as ApiProblem | undefined;
        this.rateError.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Could not look up the exchange rate.');
      },
    });
  }

  productDetail(productId: string): PurchaseProduct | undefined {
    return this.productCache().get(productId);
  }

  /** Active, purchasable units of the product, in hierarchy order. */
  unitOptions(productId: string): PurchaseProductUnit[] {
    return purchaseUnitOptions(this.productDetail(productId)?.units);
  }

  onProductChange(row: LineGroup, productId: string): void {
    if (!productId) return;
    const cached = this.productCache().get(productId);
    if (cached) {
      this.applyDefaultUnit(row, cached);
      return;
    }
    row.controls.productUnitId.setValue('');
    this.productsService.get(productId).subscribe({
      next: (detail) => {
        const next = new Map(this.productCache());
        next.set(productId, detail);
        this.productCache.set(next);
        // The user may have picked another product while this one was loading.
        if (row.controls.productId.value === productId) this.applyDefaultUnit(row, detail);
      },
    });
  }

  private applyDefaultUnit(row: LineGroup, product: PurchaseProduct): void {
    row.controls.productUnitId.setValue(defaultPurchaseUnit(product.units)?.id ?? '');
    this.applyQtyRules(row);
  }

  readonly quantityStep = quantityStep;
  readonly formatQuantity = formatQuantity;

  /** Decimal places of the unit currently selected on a row (3 until a unit is chosen). */
  unitDecimals(row: LineGroup): number {
    const unit = this.productDetail(row.controls.productId.value)?.units.find((u) => u.id === row.controls.productUnitId.value);
    return unit?.decimalPlaces ?? 3;
  }

  /** Re-apply the quantity validators from the selected unit's decimal places (mirrors the server rule). */
  applyQtyRules(row: LineGroup): void {
    row.controls.qty.setValidators(quantityValidators(this.unitDecimals(row)));
    row.controls.qty.updateValueAndValidity();
  }

  addLine(): void {
    const group = this.fb.nonNullable.group({
        productId: ['', Validators.required],
        productUnitId: ['', Validators.required],
        qty: [null as number | null, quantityValidators(3)],
        rate: [null as number | null, [Validators.required, Validators.min(0)]],
        otherCharges: [0],
        discount: [0],
        supplierBatchNumber: ['', [Validators.maxLength(60)]],
      });
    group.controls.productUnitId.valueChanges.subscribe(() => this.applyQtyRules(group));
    this.lines.push(group);
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
    this.saveWarnings.set([]);
    if (this.form.invalid || this.previews().some((p) => p.baseDecimalsError)) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const v = this.form.getRawValue();
    const body = {
        supplierId: v.supplierId,
        purchaseDate: v.purchaseDate,
        supplierInvoiceNumber: v.supplierInvoiceNumber || null,
        notes: v.notes || null,
        paymentMethod: v.paymentMethod,
        paidAmount: v.paidAmount,
        accountId: v.accountId || null,
        stockLocationId: this.locationId(),
        ratesIncludeTax: v.ratesIncludeTax,
        ...billDiscountFields(this.billDiscountMode(), v.billDiscountPercent, v.billDiscountAmount),
        billCharges: v.billCharges ?? 0,
        lines: v.lines.map((l) => ({
          productId: l.productId,
          productUnitId: l.productUnitId,
          qty: l.qty!,
          rate: l.rate!,
          otherCharges: l.otherCharges,
          discount: l.discount,
          supplierBatchNumber: l.supplierBatchNumber.trim() === '' ? null : l.supplierBatchNumber,
        })),
      };
    this.purchasesService
      .create(body, this.submission.keyFor(body))
      .subscribe({
        next: (purchase) => {
          this.saving.set(false);
          this.saved = true;
          const warnings = (purchase.warnings ?? []).map((w: PurchaseWarning) => warningLabel(w.field, w.message));
          this.saveWarnings.set(warnings);
          this.toast.show(`${purchase.purchaseNo} received · stock updated.`);
          // The detail screen shows the warnings once (GET returns none).
          this.router.navigate(['/purchases', purchase.id], warnings.length > 0 ? { state: { warnings } } : undefined);
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          const { locationError, rest } = splitLocationError(err.error as ApiProblem | undefined);
          this.locationError.set(locationError);
          const hasFieldErrors = Object.keys(rest?.errors ?? {}).length > 0;
          this.serverError.set(
            hasFieldErrors ? applyPurchaseProblem(this.form, rest, this.billDiscountMode()) : locationError ?? problemMessage(rest, 'Something went wrong.'),
          );
        },
      });
  }
}

function today(): string {
  return new Date().toISOString().slice(0, 10);
}
