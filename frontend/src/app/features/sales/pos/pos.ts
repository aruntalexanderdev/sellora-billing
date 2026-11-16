import { HttpErrorResponse } from '@angular/common/http';
import { IdempotentSubmission } from '../../../core/idempotency';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
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
import { BASE_CURRENCY_DECIMALS, discountExceedsGross, discountFromPercent, discountWithinGross, hasTooManyDecimals, isValidPercent, lineAmounts, roundMoney } from '../../../shared/money/money';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { SalesService } from '../sales.service';
import { CustomersService } from '../customers/customers.service';
import type { CustomerItem } from '../customers/customers.models';
import type { SaleCatalogProduct, SaleCatalogUnit } from '../sales.models';
import { defaultSaleUnit, isPriced, needsPriceOverride } from './pos-units';
import { convert, formatQuantity } from '../../../shared/units/unit-conversion';
import { quantityStep, quantityValidators } from '../../../shared/forms/quantity-input';
import { AuthService } from '../../../core/auth/auth.service';
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
  taxRatePercent: number;
  taxableValue: number;
  taxAmount: number;
  lineTotal: number;
  availableOnHand: number;
  exceedsStock: boolean;
}

const EMPTY_PREVIEW: LinePreview = {
  baseQty: 0,
  baseUnitLabel: '',
  taxRatePercent: 0,
  taxableValue: 0,
  taxAmount: 0,
  lineTotal: 0,
  availableOnHand: 0,
  exceedsStock: false,
};

@Component({
  selector: 'app-pos',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, UiBadge, LocationSelect],
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
    lines: this.fb.array<LineGroup>([]),
  });

  get lines(): FormArray<LineGroup> {
    return this.form.controls.lines;
  }

  private readonly linesValue = toSignal(this.lines.valueChanges, { initialValue: this.lines.value });
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

      // Same per-line rounding as the server: taxable, tax and total each rounded (half away from zero).
      const amounts = lineAmounts(row.qty, row.unitPrice, row.discount, product.taxRatePercent);
      const taxableValue = amounts.taxable;
      const taxAmount = amounts.tax;
      const totalRequested = requestedByProduct.get(productId) ?? 0;

      return {
        baseQty: baseQtys[i],
        baseUnitLabel: product.baseUnitLabel,
        taxRatePercent: product.taxRatePercent,
        taxableValue,
        taxAmount,
        lineTotal: amounts.total,
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

  readonly completeSaleDisabledReason = computed(() => {
    if (this.linesValue().length === 0) return 'Add at least one item to the cart first.';
    if (this.lineErrors().some((e) => !!e)) return 'Fix the highlighted items — a unit has no price or a price is not allowed.';
    if (this.hasStockError()) return 'Reduce quantity — not enough stock for one or more items.';
    return null;
  });

  readonly totals = computed(() => {
    const rows = this.linesValue();
    const previews = this.previews();
    let subtotal = 0,
      discount = 0,
      tax = 0,
      grandTotal = 0;
    rows.forEach((row, i) => {
      subtotal += roundMoney((row?.qty ?? 0) * (row?.unitPrice ?? 0));
      discount += roundMoney(row?.discount ?? 0);
      tax += previews[i]?.taxAmount ?? 0;
      grandTotal += previews[i]?.lineTotal ?? 0;
    });
    // Invoice totals are sums of the rounded lines.
    return { subtotal: roundMoney(subtotal), discount: roundMoney(discount), tax: roundMoney(tax), grandTotal: roundMoney(grandTotal) };
  });

  readonly dueAmount = computed(() => Math.max(0, roundMoney(this.totals().grandTotal - (this.paidAmountValue() ?? 0))));
  readonly changeAmount = computed(() => Math.max(0, roundMoney((this.paidAmountValue() ?? 0) - this.totals().grandTotal)));

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

    this.saving.set(true);
    const v = this.form.getRawValue();
    const body = {
        customerId: v.customerId,
        invoiceDate: v.invoiceDate,
        paymentMethod: v.paymentMethod,
        paidAmount: v.paidAmount,
        accountId: v.accountId || null,
        stockLocationId: this.committedLocation,
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
          this.router.navigate(['/sales', invoice.id]);
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          const { locationError, rest } = splitLocationError(err.error as ApiProblem | undefined);
          if (locationError) this.locationError.set(locationError);
          // Field errors (key "Lines") carry the specific message; the ProblemDetails title is generic.
          const message = Object.values(rest?.errors ?? {})[0]?.[0] ?? locationError ?? problemMessage(rest, 'Something went wrong.');
          this.serverError.set(message);
          if (/price of .* is now/i.test(message)) this.refreshPrices();
        },
      });
  }
}

function today(): string {
  return new Date().toISOString().slice(0, 10);
}
