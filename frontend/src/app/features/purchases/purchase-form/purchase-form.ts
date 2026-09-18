import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../shared/ui/button/button';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { PurchasesService } from '../purchases.service';
import { SuppliersService } from '../suppliers/suppliers.service';
import { ProductsService } from '../../inventory/products/products.service';
import type { SupplierItem } from '../suppliers/suppliers.models';
import type { ProductDetail } from '../../inventory/products/products.models';
import type { ApiProblem } from '../../../core/auth/auth.models';
import type { PaymentMethod } from '../purchases.models';
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
  baseQty: number;
  baseUnitLabel: string;
  taxableValue: number;
  taxAmount: number;
  lineTotal: number;
}

const EMPTY_PREVIEW: LinePreview = { baseQty: 0, baseUnitLabel: '', taxableValue: 0, taxAmount: 0, lineTotal: 0 };

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
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly paymentMethods: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'];
  readonly suppliers = signal<SupplierItem[]>([]);
  readonly products = signal<{ id: string; name: string; sku: string }[]>([]);
  private readonly productCache = signal<Map<string, ProductDetail>>(new Map());

  readonly form = this.fb.nonNullable.group({
    supplierId: ['', Validators.required],
    purchaseDate: [today(), Validators.required],
    supplierInvoiceNumber: [''],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
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
      const taxableValue = row.qty * row.rate + (row.otherCharges ?? 0) - (row.discount ?? 0);
      const taxAmount = (taxableValue * product.taxRatePercent) / 100;
      return { baseQty, baseUnitLabel: product.baseUnitLabel, taxableValue, taxAmount, lineTotal: taxableValue + taxAmount };
    });
  });

  readonly totals = computed(() => {
    const rows = this.linesValue();
    const previews = this.previews();
    let subtotal = 0, otherCharges = 0, discount = 0, tax = 0;
    rows.forEach((row, i) => {
      subtotal += (row?.qty ?? 0) * (row?.rate ?? 0);
      otherCharges += row?.otherCharges ?? 0;
      discount += row?.discount ?? 0;
      tax += previews[i]?.taxAmount ?? 0;
    });
    return { subtotal, otherCharges, discount, tax, grandTotal: subtotal + otherCharges - discount + tax };
  });

  readonly dueAmount = computed(() => Math.max(0, this.totals().grandTotal - (this.paidAmountValue() ?? 0)));

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
    this.suppliersService.list().subscribe({ next: (res) => this.suppliers.set(res.items.filter((s) => s.isActive)) });
    this.productsService.list().subscribe({
      next: (res) => this.products.set(res.items.filter((p) => p.isActive).map((p) => ({ id: p.id, name: p.name, sku: p.sku }))),
    });
    this.addLine();
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
          this.serverError.set(problem?.title ?? Object.values(problem?.errors ?? {})[0]?.[0] ?? 'Something went wrong.');
        },
      });
  }
}

function today(): string {
  return new Date().toISOString().slice(0, 10);
}
