import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { AuthService } from '../../../../core/auth/auth.service';
import { splitLocationError } from '../../../../shared/locations/location-errors';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { PurchasesService } from '../../purchases.service';
import { CashBankService, defaultAccountId } from '../../../accounts/cash-bank/cash-bank.service';
import type { AccountOption } from '../../../accounts/cash-bank/cash-bank.models';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { quantityStep } from '../../../../shared/forms/quantity-input';
import { defaultReturnUnit, proratedCredit, returnAllChoice, returnBaseQty, returnQtyError } from '../../purchase-form/purchase-units';
import type { PaymentMethod, PurchaseDetail, PurchaseLine, PurchaseReturnUnit } from '../../purchases.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

const FORMAT = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const roundTo = (n: number, places: number) => {
  const f = 10 ** places;
  return (Math.sign(n) * Math.round(Math.abs(n) * f + Number.EPSILON)) / f;
};
const todayIso = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

@Component({
  selector: 'app-purchase-return-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './purchase-return-form.html',
  styleUrl: '../../purchase-detail/purchase-detail.scss',
})
export class PurchaseReturnForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly purchasesService = inject(PurchasesService);
  private readonly cashBank = inject(CashBankService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  // One key per return form, so a double-click or retry can't remove the stock twice.
  private readonly submission = new IdempotentSubmission();

  readonly paymentMethods: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'];

  readonly purchase = signal<PurchaseDetail | null>(null);
  readonly accountOptions = signal<AccountOption[]>([]);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  private saved = false;

  /** Location the goods are returned from (null = server default). Changing it reloads the returnable quantities there. */
  readonly returnLocationId = signal<string | null>(null);
  readonly locationError = signal<string | null>(null);
  /** Locations holding stock of this purchase's batches, from `lines[].locationBalances` (distinct, first-seen order). */
  readonly locationOptions = computed(() => {
    const seen = new Map<string, { id: string; code: string; name: string }>();
    for (const l of this.purchase()?.lines ?? []) {
      for (const b of l.locationBalances ?? []) {
        if (b.baseQty > 0 && !seen.has(b.stockLocationId)) seen.set(b.stockLocationId, { id: b.stockLocationId, code: b.code, name: b.name });
      }
    }
    return [...seen.values()];
  });
  /** O5: only for StockLocations.View holders, and only when there is more than one location to choose. */
  readonly showLocationPicker = computed(() => this.auth.hasPermission('StockLocations.View') && this.locationOptions().length > 1);

  readonly base = (n: number) => (this.purchase()?.baseCurrencySymbol ?? '₹') + FORMAT(n);
  readonly original = (n: number) => (this.purchase()?.currencySymbol ?? '') + FORMAT(n);

  readonly formatQuantity = formatQuantity;
  readonly quantityStep = quantityStep;

  /** Quantity being returned per purchase line, keyed by line id. */
  readonly qty = signal<Record<string, number | null>>({});
  /** Chosen return unit per purchase line (absent = the line's own unit). */
  readonly unitChoice = signal<Record<string, string>>({});

  /** The unit a line's return is entered in. */
  unitFor(l: PurchaseLine): PurchaseReturnUnit | undefined {
    const id = this.unitChoice()[l.id];
    return l.returnUnits.find((u) => u.productUnitId === id) ?? defaultReturnUnit(l.returnUnits);
  }

  /** Client-side error for the quantity entered on a line, against the chosen unit's max (null when fine). */
  qtyError(l: PurchaseLine): string | null {
    return returnQtyError(this.qty()[l.id], this.unitFor(l), l.baseUnitDecimalPlaces, l.baseUnitLabel);
  }

  readonly form = this.fb.nonNullable.group({
    returnDate: [todayIso(), Validators.required],
    reason: [''],
  });

  readonly refundNow = signal(false);
  readonly refundForm = this.fb.nonNullable.group({
    amount: [null as number | null, [Validators.min(0.000001)]],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
    accountId: [''],
  });

  // Goods already sold can't go back, so a line may be returnable only in part — or not at all.
  readonly lines = computed(() => this.purchase()?.lines.filter((l) => l.returnableBaseQty > 0 && l.returnUnits.length > 0) ?? []);
  readonly blocked = computed(() => (this.purchase()?.lines ?? []).filter((l) => l.returnableBaseQty <= 0 && l.returnedBaseQty < l.baseQty));

  /** Estimates of the credit, in the base currency and the purchase's own currency. The server works the exact figures. */
  readonly creditBase = computed(() => {
    let total = 0;
    for (const l of this.lines()) {
      const q = this.qty()[l.id] ?? 0;
      const unit = this.unitFor(l);
      // Prorated by base quantity, the same way the server splits the credit.
      if (q > 0 && unit) total += proratedCredit(l.lineTotal, returnBaseQty(q, unit), l.baseQty);
    }
    return roundTo(total, this.purchase()?.baseCurrencyDecimalPrecision ?? 2);
  });
  readonly creditOriginal = computed(() => {
    const p = this.purchase();
    if (!p || p.grandTotal <= 0) return 0;
    return roundTo((this.creditBase() / p.grandTotal) * p.originalGrandTotal, p.currencyDecimalPrecision);
  });

  readonly chosen = computed(() => this.lines().filter((l) => (this.qty()[l.id] ?? 0) > 0));
  readonly linesValid = computed(() => this.chosen().length > 0 && this.lines().every((l) => !this.qtyError(l)));

  /** What the supplier would then hold beyond what is owed, in the purchase's currency. */
  readonly refundableAfter = computed(() => {
    const p = this.purchase();
    if (!p) return 0;
    return Math.max(0, roundTo(p.originalPaidAmount - (p.originalNetTotal - this.creditOriginal()), p.currencyDecimalPrecision));
  });

  readonly amountStep = computed(() => String(1 / 10 ** (this.purchase()?.currencyDecimalPrecision ?? 2)));

  hasUnsavedChanges(): boolean {
    return !this.saved && (this.chosen().length > 0 || this.form.dirty);
  }

  ngOnInit(): void {
    this.cashBank.options().subscribe({ next: (o) => this.accountOptions.set(o) });
    this.refundForm.controls.paymentMethod.valueChanges.subscribe((m) =>
      this.refundForm.patchValue({ accountId: defaultAccountId(this.accountOptions(), m) }),
    );

    this.loadPurchase(null, true);
  }

  private loadPurchase(locationId: string | null, initial: boolean): void {
    this.purchasesService.get(this.route.snapshot.paramMap.get('id')!, locationId).subscribe({
      next: (p) => {
        this.purchase.set(p);
        if (initial) {
          this.refundForm.patchValue({ paymentMethod: p.paymentMethod, accountId: defaultAccountId(this.accountOptions(), p.paymentMethod) });
        }
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  /** Returnable quantity depends on the location, so entered quantities are cleared and the purchase is reloaded there. */
  onLocationChange(raw: string): void {
    const id = raw || null;
    if (id === this.returnLocationId()) return;
    this.returnLocationId.set(id);
    this.locationError.set(null);
    this.qty.set({});
    this.unitChoice.set({});
    this.loadPurchase(id, false);
  }

  setQty(id: string, value: string): void {
    this.qty.update((q) => ({ ...q, [id]: value === '' ? null : Number(value) }));
  }

  setUnit(id: string, productUnitId: string): void {
    this.unitChoice.update((c) => ({ ...c, [id]: productUnitId }));
  }

  returnAll(): void {
    const qty: Record<string, number | null> = {};
    const units: Record<string, string> = {};
    for (const l of this.lines()) {
      const choice = returnAllChoice(l);
      if (!choice) continue;
      qty[l.id] = choice.qty;
      units[l.id] = choice.productUnitId;
    }
    this.qty.set(qty);
    this.unitChoice.set(units);
  }

  toggleRefund(checked: boolean): void {
    this.refundNow.set(checked);
    if (checked) {
      this.refundForm.patchValue({
        amount: this.refundableAfter(),
        accountId: defaultAccountId(this.accountOptions(), this.refundForm.controls.paymentMethod.value),
      });
    }
  }

  submit(): void {
    this.error.set(null);
    if (this.saving()) return;
    const p = this.purchase();
    if (!p || !this.linesValid() || this.form.invalid) {
      this.form.markAllAsTouched();
      this.error.set(this.chosen().length === 0 ? 'Enter a quantity for at least one item.' : 'Check the quantities entered.');
      return;
    }
    const r = this.refundForm.getRawValue();
    if (this.refundNow() && (r.amount == null || roundTo(r.amount, p.currencyDecimalPrecision) <= 0 || roundTo(r.amount, p.currencyDecimalPrecision) > this.refundableAfter())) {
      this.error.set(`The refund must be more than zero and no more than ${this.original(this.refundableAfter())}.`);
      return;
    }

    const v = this.form.getRawValue();
    this.saving.set(true);
    const body = {
        purchaseOrderId: p.id,
        returnDate: v.returnDate,
        reason: v.reason || null,
        stockLocationId: this.returnLocationId(),
        lines: this.chosen().map((l) => ({ purchaseOrderLineId: l.id, productUnitId: this.unitFor(l)?.productUnitId ?? null, qty: this.qty()[l.id]! })),
        refund: this.refundNow() ? { amount: r.amount!, paymentMethod: r.paymentMethod, accountId: r.accountId || null } : null,
      };
    this.purchasesService
      .createReturn(body, this.submission.keyFor(body))
      .subscribe({
        next: (created) => {
          this.saving.set(false);
          this.saved = true;
          this.toast.show(`${created.returnNo} recorded — stock removed.`);
          this.router.navigate(['/purchases/returns', created.id]);
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          const { locationError, rest: problem } = splitLocationError(err.error as ApiProblem | undefined);
          this.locationError.set(locationError);
          this.error.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? locationError ?? problem?.title ?? 'Could not record this return.');
        },
      });
  }
}
