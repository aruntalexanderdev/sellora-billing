import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { merge } from 'rxjs';
import { FormArray, FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../shared/ui/button/button';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { PurchasesService } from '../purchases.service';
import { SuppliersService } from '../suppliers/suppliers.service';
import { ProductsService } from '../../inventory/products/products.service';
import { ExchangeRatesService } from '../../administration/exchange-rates/exchange-rates.service';
import type { ResolvedExchangeRate } from '../../administration/exchange-rates/exchange-rates.models';
import type { SupplierItem } from '../suppliers/suppliers.models';
import type { ProductDetail } from '../../inventory/products/products.models';
import type { ApiProblem } from '../../../core/auth/auth.models';
import type { PaymentMethod } from '../purchases.models';
import { CashBankService, defaultAccountId } from '../../accounts/cash-bank/cash-bank.service';
import type { AccountOption } from '../../accounts/cash-bank/cash-bank.models';

import type { HasUnsavedChanges } from '../../../core/unsaved-changes.guard';

type LineGroup = FormGroup<{
  productId: FormControl<string>;
  sellingModeId: FormControl<string>;
  qty: FormControl<number | null>;
  rate: FormControl<number | null>;
  otherCharges: FormControl<number>;
  discount: FormControl<number>;
}>;

interface LinePreview {
  valid: boolean;
  baseQty: number;
  baseUnitLabel: string;
  subtotal: number;
  otherCharges: number;
  discount: number;
  taxableValue: number;
  taxAmount: number;
  lineTotal: number;
  // The same line converted to the base currency (only meaningful for a foreign supplier).
  baseTotal: number;
}

const EMPTY_PREVIEW: LinePreview = {
  valid: false, baseQty: 0, baseUnitLabel: '', subtotal: 0, otherCharges: 0, discount: 0,
  taxableValue: 0, taxAmount: 0, lineTotal: 0, baseTotal: 0,
};

const roundTo = (n: number, places: number) => {
  const f = 10 ** places;
  return Math.sign(n) * Math.round(Math.abs(n) * f + Number.EPSILON) / f;
};

@Component({
  selector: 'app-purchase-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './purchase-form.html',
  styleUrl: './purchase-form.scss',
})
export class PurchaseForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly purchasesService = inject(PurchasesService);
  private readonly suppliersService = inject(SuppliersService);
  private readonly productsService = inject(ProductsService);
  private readonly exchangeRatesService = inject(ExchangeRatesService);
  private readonly cashBank = inject(CashBankService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly paymentMethods: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'];
  readonly accountOptions = signal<AccountOption[]>([]);
  readonly suppliers = signal<SupplierItem[]>([]);
  readonly products = signal<{ id: string; name: string; sku: string }[]>([]);
  private readonly productCache = signal<Map<string, ProductDetail>>(new Map());

  readonly form = this.fb.nonNullable.group({
    supplierId: ['', Validators.required],
    purchaseDate: [today(), Validators.required],
    supplierInvoiceNumber: [''],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
    accountId: [''],
    paidAmount: [0, [Validators.required, Validators.min(0)]],
    notes: [''],
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
    const cache = this.productCache();
    return this.linesValue().map((row) => {
      const product = row?.productId ? cache.get(row.productId) : undefined;
      const mode = product?.sellingModes.find((m) => m.id === row?.sellingModeId);
      if (!product || !mode || !row?.qty || row.rate == null) return EMPTY_PREVIEW;

      const baseQty = row.qty * mode.conversionFactorToBase;

      // Mirrors the server: for a foreign supplier every invoice figure (tax included) is rounded to the
      // supplier currency's precision, and each rounded figure is then converted and rounded to the base
      // currency's precision — so the preview matches what is saved.
      const foreign = this.isForeign();
      const places = this.selectedSupplier()?.currencyDecimalPrecision ?? 2;
      const basePlaces = this.resolvedRate()?.baseCurrencyDecimalPrecision ?? 2;
      const fx = this.resolvedRate()?.rateToBase ?? 1;
      const inOriginal = (n: number) => (foreign ? roundTo(n, places) : n);
      const toBase = (n: number) => (foreign ? roundTo(n * fx, basePlaces) : n);

      const subtotal = inOriginal(row.qty * row.rate);
      const otherCharges = inOriginal(row.otherCharges ?? 0);
      const discount = inOriginal(row.discount ?? 0);
      const taxableValue = subtotal + otherCharges - discount;
      const taxAmount = inOriginal((taxableValue * product.taxRatePercent) / 100);
      const baseTotal = toBase(subtotal) + toBase(otherCharges) - toBase(discount) + toBase(taxAmount);
      return {
        valid: true, baseQty, baseUnitLabel: product.baseUnitLabel, subtotal, otherCharges, discount,
        taxableValue, taxAmount, lineTotal: taxableValue + taxAmount, baseTotal,
      };
    });
  });

  readonly totals = computed(() => {
    const rows = this.linesValue();
    const previews = this.previews();
    let subtotal = 0, otherCharges = 0, discount = 0, tax = 0;
    rows.forEach((row, i) => {
      const preview = previews[i];
      subtotal += preview?.valid ? preview.subtotal : (row?.qty ?? 0) * (row?.rate ?? 0);
      otherCharges += preview?.valid ? preview.otherCharges : row?.otherCharges ?? 0;
      discount += preview?.valid ? preview.discount : row?.discount ?? 0;
      tax += preview?.taxAmount ?? 0;
    });
    return { subtotal, otherCharges, discount, tax, grandTotal: subtotal + otherCharges - discount + tax };
  });

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

  // Sum of each line converted on its own, exactly as the server totals them.
  readonly baseGrandTotal = computed(() => this.previews().reduce((sum, p) => sum + p.baseTotal, 0));
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
        this.accountOptions.set(o);
        this.form.patchValue({ accountId: defaultAccountId(o, this.form.controls.paymentMethod.value) });
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

  productDetail(productId: string): ProductDetail | undefined {
    return this.productCache().get(productId);
  }

  onProductChange(row: LineGroup, productId: string): void {
    if (!productId) return;
    if (this.productCache().has(productId)) {
      this.applyFirstMode(row, this.productCache().get(productId)!);
      return;
    }
    this.productsService.get(productId).subscribe({
      next: (detail) => {
        const next = new Map(this.productCache());
        next.set(productId, detail);
        this.productCache.set(next);
        this.applyFirstMode(row, detail);
      },
    });
  }

  private applyFirstMode(row: LineGroup, product: ProductDetail): void {
    const firstMode = product.sellingModes.find((m) => m.isActive) ?? product.sellingModes[0];
    row.controls.sellingModeId.setValue(firstMode?.id ?? '');
  }

  addLine(): void {
    this.lines.push(
      this.fb.nonNullable.group({
        productId: ['', Validators.required],
        sellingModeId: ['', Validators.required],
        qty: [null as number | null, [Validators.required, Validators.min(0.001)]],
        rate: [null as number | null, [Validators.required, Validators.min(0)]],
        otherCharges: [0],
        discount: [0],
      }),
    );
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

    this.saving.set(true);
    const v = this.form.getRawValue();
    this.purchasesService
      .create({
        supplierId: v.supplierId,
        purchaseDate: v.purchaseDate,
        supplierInvoiceNumber: v.supplierInvoiceNumber || null,
        notes: v.notes || null,
        paymentMethod: v.paymentMethod,
        paidAmount: v.paidAmount,
        accountId: v.accountId || null,
        lines: v.lines.map((l) => ({
          productId: l.productId,
          sellingModeId: l.sellingModeId,
          qty: l.qty!,
          rate: l.rate!,
          otherCharges: l.otherCharges,
          discount: l.discount,
        })),
      })
      .subscribe({
        next: (purchase) => {
          this.saving.set(false);
          this.saved = true;
          this.toast.show(`${purchase.purchaseNo} received · stock updated.`);
          this.router.navigate(['/purchases', purchase.id]);
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          const problem = err.error as ApiProblem | undefined;
          this.serverError.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Something went wrong.');
        },
      });
  }
}

function today(): string {
  return new Date().toISOString().slice(0, 10);
}
