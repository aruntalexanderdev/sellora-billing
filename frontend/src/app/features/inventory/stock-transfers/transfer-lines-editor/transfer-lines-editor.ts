import { Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { quantityStep, quantityValidators } from '../../../../shared/forms/quantity-input';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { ProductsService } from '../../products/products.service';
import { ProductUnitsLookup, toUnitRefs } from '../product-units-lookup';
import type { UnitRef } from '../stock-transfer-rules';

export type TransferLineGroup = FormGroup<{
  productId: FormControl<string>;
  productUnitId: FormControl<string>;
  quantity: FormControl<number | null>;
}>;
export type TransferLines = FormArray<TransferLineGroup>;

export function newTransferLine(fb: FormBuilder, value: { productId?: string; productUnitId?: string; quantity?: number | null } = {}): TransferLineGroup {
  return fb.nonNullable.group({
    productId: [value.productId ?? '', Validators.required],
    productUnitId: [value.productUnitId ?? '', Validators.required],
    quantity: [value.quantity ?? (null as number | null), quantityValidators(3)],
  });
}

interface PickerProduct {
  id: string;
  name: string;
  sku: string;
}

/**
 * Product + unit + quantity rows for a transfer (form and van wizard). Units load per product (cache); the unit defaults to the
 * base unit; quantity `step`/validators follow the selected unit's decimalPlaces. Server errors sit on the row controls.
 */
@Component({
  selector: 'app-transfer-lines-editor',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './transfer-lines-editor.html',
  styleUrl: './transfer-lines-editor.scss',
})
export class TransferLinesEditor implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly products = inject(ProductsService);
  private readonly lookup = inject(ProductUnitsLookup);
  private readonly destroyRef = inject(DestroyRef);

  readonly lines = input.required<TransferLines>();
  readonly pickerProducts = signal<PickerProduct[]>([]);
  private readonly unitsByProduct = signal<Record<string, UnitRef[]>>({});
  private readonly baseUnitByProduct = new Map<string, string>();
  private readonly requested = new Set<string>();
  private readonly appliedDecimals = new WeakMap<object, number>();

  ngOnInit(): void {
    this.products.list().subscribe({ next: (res) => this.pickerProducts.set(res.items.filter((p) => p.isActive).map((p) => ({ id: p.id, name: p.name, sku: p.sku }))) });
    this.sync();
    this.lines().valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.sync());
  }

  add(): void {
    this.lines().push(newTransferLine(this.fb));
  }

  remove(i: number): void {
    if (this.lines().length > 1) this.lines().removeAt(i);
  }

  unitsFor(row: TransferLineGroup): UnitRef[] {
    return this.unitsByProduct()[row.controls.productId.value] ?? [];
  }

  step(row: TransferLineGroup): string {
    return quantityStep(this.selectedUnit(row)?.decimalPlaces ?? 3);
  }

  preview(row: TransferLineGroup): string | null {
    const unit = this.selectedUnit(row);
    const qty = row.controls.quantity.value;
    if (!unit || qty === null || !(qty > 0) || unit.conversionFactorToBase === 1) return null;
    return `1 ${unit.unitCode} = ${formatQuantity(unit.conversionFactorToBase, 0)} base`;
  }

  error(row: TransferLineGroup, field: 'productId' | 'productUnitId' | 'quantity'): string | null {
    const c = row.controls[field];
    const errors = c.errors;
    if (!errors) return null;
    if (errors['server']) return errors['server'];
    if (!c.touched) return null;
    if (field === 'productId') return 'Select a product.';
    if (field === 'productUnitId') return 'Select a unit.';
    const unit = this.selectedUnit(row);
    if (errors['maxDecimalPlaces']) {
      const max = errors['maxDecimalPlaces'].max as number;
      return max === 0 ? `${unit?.displayName ?? 'This unit'} takes whole numbers only.` : `${unit?.displayName ?? 'This unit'} allows at most ${max} decimal place${max === 1 ? '' : 's'}.`;
    }
    return 'Enter a quantity greater than zero.';
  }

  private selectedUnit(row: TransferLineGroup): UnitRef | null {
    return this.unitsFor(row).find((u) => u.id === row.controls.productUnitId.value) ?? null;
  }

  /** Loads units for newly chosen products, defaults/repairs the unit and applies decimal validators. */
  private sync(): void {
    for (const row of this.lines().controls) {
      const productId = row.controls.productId.value;
      if (!productId) continue;
      if (!this.requested.has(productId)) {
        this.requested.add(productId);
        this.lookup.units(productId).subscribe({
          next: (all) => {
            const active = all.filter((u) => u.isActive);
            this.baseUnitByProduct.set(productId, (active.find((u) => u.isBaseUnit) ?? active[0])?.id ?? '');
            const keep = this.lines().controls.map((r) => r.controls.productUnitId.value);
            this.unitsByProduct.update((m) => ({ ...m, [productId]: toUnitRefs(all, keep) }));
            this.sync();
          },
          error: () => this.requested.delete(productId),
        });
        continue;
      }
      const units = this.unitsByProduct()[productId];
      if (!units) continue;
      const unitId = row.controls.productUnitId.value;
      if (!units.some((u) => u.id === unitId)) {
        row.controls.productUnitId.setValue(this.baseUnitByProduct.get(productId) ?? units[0]?.id ?? '', { emitEvent: false });
      }
      const dp = this.selectedUnit(row)?.decimalPlaces ?? 3;
      if (this.appliedDecimals.get(row) !== dp) {
        this.appliedDecimals.set(row, dp);
        row.controls.quantity.setValidators(quantityValidators(dp));
        row.controls.quantity.updateValueAndValidity({ emitEvent: false });
      }
    }
  }
}
