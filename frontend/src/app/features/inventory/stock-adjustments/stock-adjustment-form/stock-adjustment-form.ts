import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { StockAdjustmentsService } from '../stock-adjustments.service';
import { StockService } from '../../stock/stock.service';
import { ProductsService } from '../../products/products.service';
import type { AdjustmentDirection, AdjustmentType } from '../stock-adjustments.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';

const REQUIRED_DIRECTION: Record<AdjustmentType, AdjustmentDirection | null> = {
  Damage: 'Decrease',
  Loss: 'Decrease',
  Expiry: 'Decrease',
  OpeningStock: 'Increase',
  Correction: null,
};

@Component({
  selector: 'app-stock-adjustment-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './stock-adjustment-form.html',
  styleUrl: './stock-adjustment-form.scss',
})
export class StockAdjustmentForm implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly adjustmentsService = inject(StockAdjustmentsService);
  private readonly stockService = inject(StockService);
  private readonly productsService = inject(ProductsService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly types: AdjustmentType[] = ['Damage', 'Loss', 'Correction', 'Expiry', 'OpeningStock'];
  readonly products = signal<{ id: string; name: string; sku: string }[]>([]);
  readonly currentQty = signal<number | null>(null);
  readonly baseUnitLabel = signal('');

  readonly form = this.fb.nonNullable.group({
    productId: ['', Validators.required],
    type: ['Correction' as AdjustmentType, Validators.required],
    direction: ['Decrease' as AdjustmentDirection, Validators.required],
    quantityBaseUnits: [null as number | null, [Validators.required, Validators.min(0.001)]],
    unitCost: [null as number | null],
    reason: [''],
  });

  private readonly formValue = toSignal(this.form.valueChanges, { initialValue: this.form.value });

  readonly requiredDirection = computed(() => REQUIRED_DIRECTION[this.formValue().type ?? 'Correction']);
  readonly isIncrease = computed(() => this.formValue().direction === 'Increase');
  readonly resultingQty = computed(() => {
    const current = this.currentQty();
    const qty = this.formValue().quantityBaseUnits;
    if (current == null || qty == null) return null;
    return this.isIncrease() ? current + qty : current - qty;
  });

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);

  // Reactive forms ignore a template [disabled] binding on a formControlName element and keep
  // resyncing it to the control's own enable/disable state — so the lock has to be applied here.
  private readonly directionLockEffect = effect(() => {
    const required = this.requiredDirection();
    const direction = this.form.controls.direction;
    if (required) {
      if (direction.value !== required) direction.setValue(required, { emitEvent: false });
      if (direction.enabled) direction.disable({ emitEvent: false });
    } else if (direction.disabled) {
      direction.enable({ emitEvent: false });
    }
  });

  ngOnInit(): void {
    this.productsService.list().subscribe({
      next: (res) => {
        this.products.set(res.items.filter((p) => p.isActive).map((p) => ({ id: p.id, name: p.name, sku: p.sku })));
        const preselect = this.route.snapshot.queryParamMap.get('productId');
        if (preselect) {
          this.form.controls.productId.setValue(preselect);
          this.onProductChange(preselect);
        }
      },
    });
  }

  onProductChange(productId: string): void {
    if (!productId) {
      this.currentQty.set(null);
      return;
    }
    this.stockService.detail(productId).subscribe({
      next: (d) => {
        this.currentQty.set(d.quantityOnHand);
        this.baseUnitLabel.set(d.baseUnitLabel);
      },
    });
  }

  async submit(): Promise<void> {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    if (this.isIncrease() && (this.form.controls.unitCost.value === null || this.form.controls.unitCost.value < 0)) {
      this.form.controls.unitCost.setErrors({ required: true });
      this.form.controls.unitCost.markAsTouched();
      return;
    }

    const v = this.form.getRawValue();
    const productName = this.products().find((p) => p.id === v.productId)?.name ?? 'this item';
    const confirmed = await this.confirm.ask({
      title: `Post this adjustment?`,
      body: `${productName}: ${this.currentQty()?.toFixed(2)} → ${this.resultingQty()?.toFixed(2)} ${this.baseUnitLabel()}. This is a permanent, traceable stock entry.`,
      confirmLabel: 'Post adjustment',
      destructive: v.direction === 'Decrease',
    });
    if (!confirmed) return;

    this.saving.set(true);
    this.adjustmentsService
      .create({
        productId: v.productId,
        type: v.type,
        direction: v.direction,
        quantityBaseUnits: v.quantityBaseUnits!,
        unitCost: v.unitCost,
        reason: v.reason || null,
      })
      .subscribe({
        next: (result) => {
          this.saving.set(false);
          this.toast.show(`${result.adjustmentNo} posted · ${result.productName}: ${result.beforeQty} → ${result.afterQty} ${this.baseUnitLabel()}.`);
          this.router.navigate(['/inventory/stock', result.productId]);
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          const problem = err.error as ApiProblem | undefined;
          if (problem?.errors) {
            let matched = false;
            for (const [field, messages] of Object.entries(problem.errors)) {
              const control = this.form.get(field.charAt(0).toLowerCase() + field.slice(1));
              if (control) {
                control.setErrors({ server: messages[0] });
                matched = true;
              }
            }
            if (matched) return;
          }
          this.serverError.set(problem?.title ?? Object.values(problem?.errors ?? {})[0]?.[0] ?? 'Something went wrong.');
        },
      });
  }
}
