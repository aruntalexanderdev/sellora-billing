import { HttpErrorResponse } from '@angular/common/http';
import { newIdempotencyKey } from '../../../core/idempotency';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormGroup, FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../shared/ui/button/button';
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
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './pos.html',
  styleUrl: './pos.scss',
})
export class Pos implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  // One key per sale, so a double-tap on Complete can't sell the stock twice.
  private readonly idempotencyKey = newIdempotencyKey();
  private readonly salesService = inject(SalesService);
  private readonly customersService = inject(CustomersService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  /** Only holders of Sales.PriceOverride may change a price or discount; everyone else charges the list price. */
  readonly canOverridePrice = computed(() => this.auth.hasPermission('Sales.PriceOverride'));
  readonly formatQuantity = formatQuantity;
  readonly quantityStep = quantityStep;

  private readonly cashBank = inject(CashBankService);
  readonly accountOptions = signal<AccountOption[]>([]);
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

      const taxableValue = row.qty * (row.unitPrice ?? 0) - (row.discount ?? 0);
      const taxAmount = (taxableValue * product.taxRatePercent) / 100;
      const totalRequested = requestedByProduct.get(productId) ?? 0;

      return {
        baseQty: baseQtys[i],
        baseUnitLabel: product.baseUnitLabel,
        taxRatePercent: product.taxRatePercent,
        taxableValue,
        taxAmount,
        lineTotal: taxableValue + taxAmount,
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
      tax = 0;
    rows.forEach((row, i) => {
      subtotal += (row?.qty ?? 0) * (row?.unitPrice ?? 0);
      discount += row?.discount ?? 0;
      tax += previews[i]?.taxAmount ?? 0;
    });
    return { subtotal, discount, tax, grandTotal: subtotal - discount + tax };
  });

  readonly dueAmount = computed(() => Math.max(0, this.totals().grandTotal - (this.paidAmountValue() ?? 0)));
  readonly changeAmount = computed(() => Math.max(0, (this.paidAmountValue() ?? 0) - this.totals().grandTotal));

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.lines.length > 0;
  }

  ngOnInit(): void {
    this.cashBank.options().subscribe({
      next: (o) => {
        this.accountOptions.set(o);
        this.form.patchValue({ accountId: defaultAccountId(o, this.form.controls.paymentMethod.value) });
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
    this.salesService.catalog().subscribe({ next: (res) => this.catalog.set(res) });
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
      this.fb.nonNullable.group({
        productId: [product.productId, Validators.required],
        productUnitId: [unit.productUnitId, Validators.required],
        qty: [1, quantityValidators(unit.decimalPlaces)],
        unitPrice: [unit.currentPrice ?? 0, [Validators.required, Validators.min(0)]],
        discount: [0, [Validators.min(0)]],
      }),
    );
  }

  /** Enter in the search box: an exact barcode lookup resolves product and unit (weighed codes are not supported). */
  scan(event?: Event): void {
    event?.preventDefault();
    const code = this.productQuery().trim();
    if (!code) return;
    this.salesService.lookupBarcode(code).subscribe({
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
      this.salesService.catalog(sku).subscribe({
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
    this.salesService
      .create({
        customerId: v.customerId,
        invoiceDate: v.invoiceDate,
        paymentMethod: v.paymentMethod,
        paidAmount: v.paidAmount,
        accountId: v.accountId || null,
        lines: v.lines.map((l) => ({
          productId: l.productId,
          productUnitId: l.productUnitId,
          qty: l.qty,
          unitPrice: l.unitPrice,
          discount: l.discount,
          overridePrice: this.canOverridePrice() && needsPriceOverride(l.unitPrice, this.unitPriceOf(l.productId, l.productUnitId), l.discount),
        })),
      }, this.idempotencyKey)
      .subscribe({
        next: (invoice) => {
          this.saving.set(false);
          this.saved = true;
          this.toast.show(`${invoice.invoiceNo} completed · stock updated.`);
          this.router.navigate(['/sales', invoice.id]);
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          const problem = err.error as ApiProblem | undefined;
          // Field errors (key "Lines") carry the specific message; the ProblemDetails title is generic.
          const message = Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Something went wrong.';
          this.serverError.set(message);
          if (/price of .* is now/i.test(message)) this.refreshPrices();
        },
      });
  }
}

function today(): string {
  return new Date().toISOString().slice(0, 10);
}
