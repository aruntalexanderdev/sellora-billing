import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormGroup, FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../shared/ui/button/button';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { SalesService } from '../sales.service';
import { CustomersService } from '../customers/customers.service';
import type { CustomerItem } from '../customers/customers.models';
import type { SaleCatalogProduct } from '../sales.models';
import type { ApiProblem } from '../../../core/auth/auth.models';
import type { PaymentMethod } from '../sales.models';
import type { HasUnsavedChanges } from '../../../core/unsaved-changes.guard';

type LineGroup = FormGroup<{
  productId: FormControl<string>;
  sellingModeId: FormControl<string>;
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
  private readonly salesService = inject(SalesService);
  private readonly customersService = inject(CustomersService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly paymentMethods: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'];
  readonly customers = signal<CustomerItem[]>([]);
  readonly catalog = signal<SaleCatalogProduct[]>([]);
  readonly productQuery = signal('');

  readonly searchResults = computed<SaleCatalogProduct[]>(() => {
    const q = this.productQuery().trim().toLowerCase();
    if (!q) return [];
    return this.catalog()
      .filter((p) => p.name.toLowerCase().includes(q) || p.sku.toLowerCase().includes(q))
      .slice(0, 8);
  });

  readonly form = this.fb.nonNullable.group({
    customerId: ['', Validators.required],
    invoiceDate: [today()],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
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
      const mode = product?.sellingModes.find((m) => m.id === row?.sellingModeId);
      return product && mode && row?.qty ? row.qty * mode.conversionFactorToBase : 0;
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
      const mode = product?.sellingModes.find((m) => m.id === row?.sellingModeId);
      if (!product || !mode || !row?.qty || !productId) return EMPTY_PREVIEW;

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
    const firstMode = product.sellingModes.find((m) => m.conversionFactorToBase) ?? product.sellingModes[0];
    if (!firstMode) {
      this.toast.show(`${product.name} has no selling mode configured yet.`);
      return;
    }

    this.lines.push(
      this.fb.nonNullable.group({
        productId: [product.productId, Validators.required],
        sellingModeId: [firstMode.id, Validators.required],
        qty: [1, [Validators.required, Validators.min(0.001)]],
        unitPrice: [firstMode.currentPrice ?? 0, [Validators.required, Validators.min(0)]],
        discount: [0],
      }),
    );
    this.productQuery.set('');
  }

  onSellingModeChange(row: LineGroup): void {
    const product = this.catalog().find((p) => p.productId === row.controls.productId.value);
    const mode = product?.sellingModes.find((m) => m.id === row.controls.sellingModeId.value);
    row.controls.unitPrice.setValue(mode?.currentPrice ?? 0);
  }

  sellingModesFor(productId: string): SaleCatalogProduct['sellingModes'] {
    return this.catalog().find((p) => p.productId === productId)?.sellingModes ?? [];
  }

  productName(productId: string): string {
    return this.catalog().find((p) => p.productId === productId)?.name ?? '';
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
        lines: v.lines.map((l) => ({
          productId: l.productId,
          sellingModeId: l.sellingModeId,
          qty: l.qty,
          unitPrice: l.unitPrice,
          discount: l.discount,
        })),
      })
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
          this.serverError.set(problem?.title ?? Object.values(problem?.errors ?? {})[0]?.[0] ?? 'Something went wrong.');
        },
      });
  }
}

function today(): string {
  return new Date().toISOString().slice(0, 10);
}
