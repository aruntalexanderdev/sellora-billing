import { IdempotentSubmission } from '../../../../core/idempotency';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { SalesService } from '../../sales.service';
import { CashBankService, defaultAccountId } from '../../../accounts/cash-bank/cash-bank.service';
import type { AccountOption } from '../../../accounts/cash-bank/cash-bank.models';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { quantityStep } from '../../../../shared/forms/quantity-input';
import { defaultReturnUnit, proratedCredit, returnAllChoice, returnBaseQty, returnQtyError } from '../../../../shared/units/return-units';
import { estimateReturnTax } from '../../../purchases/purchase-tax';
import type { PaymentMethod, SaleReturnUnit, SalesInvoiceDetail, SalesInvoiceLine } from '../../sales.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

const INR = (n: number) => '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const cents = (n: number) => Math.round(n * 100);
const todayIso = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

@Component({
  selector: 'app-sales-return-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './sales-return-form.html',
  styleUrl: '../../../purchases/purchase-detail/purchase-detail.scss',
})
export class SalesReturnForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly salesService = inject(SalesService);
  private readonly cashBank = inject(CashBankService);
  private readonly toast = inject(ToastService);
  // One key per return form, so a double-click or retry can't restock twice. The server resolves the restock location.
  private readonly submission = new IdempotentSubmission();
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly INR = INR;
  readonly paymentMethods: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'];

  readonly invoice = signal<SalesInvoiceDetail | null>(null);
  readonly accountOptions = signal<AccountOption[]>([]);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  private saved = false;

  readonly formatQuantity = formatQuantity;
  readonly quantityStep = quantityStep;

  /** Quantity being returned per invoice line, keyed by line id. */
  readonly qty = signal<Record<string, number | null>>({});
  /** Chosen return unit per invoice line (absent = the line's own unit). */
  readonly unitChoice = signal<Record<string, string>>({});

  /** The unit a line's return is entered in. */
  unitFor(l: SalesInvoiceLine): SaleReturnUnit | undefined {
    const id = this.unitChoice()[l.id];
    return l.returnUnits.find((u) => u.productUnitId === id) ?? defaultReturnUnit(l.returnUnits);
  }

  /** Client-side error for the quantity entered on a line, against the chosen unit's max (null when fine). */
  qtyError(l: SalesInvoiceLine): string | null {
    return returnQtyError(this.qty()[l.id], this.unitFor(l), l.baseUnitDecimalPlaces, l.baseUnitLabel);
  }

  readonly form = this.fb.nonNullable.group({
    returnDate: [todayIso(), Validators.required],
    reason: [''],
  });

  readonly refundNow = signal(false);
  readonly refundForm = this.fb.nonNullable.group({
    amount: [null as number | null, [Validators.min(0.01)]],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
    accountId: [''],
  });

  readonly lines = computed(() => this.invoice()?.lines.filter((l) => l.returnableBaseQty > 0 && l.returnUnits.length > 0) ?? []);

  /** An estimate of the credit — the server works the exact figure, taking the remainder on a final return. */
  readonly credit = computed(() => {
    let total = 0;
    for (const l of this.lines()) {
      const q = this.qty()[l.id] ?? 0;
      const unit = this.unitFor(l);
      // Prorated by base quantity, the same way the server splits the credit.
      if (q > 0 && unit) total += proratedCredit(l.lineTotal, returnBaseQty(q, unit), l.baseQty);
    }
    return Math.round(total * 100) / 100;
  });

  /** Estimated taxable value and tax of the quantity entered on a line (the original rows prorated by base quantity); null when none is entered. */
  estimateFor(l: SalesInvoiceLine): { taxable: number; tax: number } | null {
    const q = this.qty()[l.id] ?? 0;
    const unit = this.unitFor(l);
    if (!(q > 0) || !unit) return null;
    const e = estimateReturnTax(l, returnBaseQty(q, unit));
    return { taxable: e.taxable, tax: e.tax };
  }

  /** Estimated tax inside the credit (labelled an estimate: the server reverses the rows exactly, the last return takes the remainder). */
  readonly taxEstimate = computed(() => Math.round(this.lines().reduce((s, l) => s + (this.estimateFor(l)?.tax ?? 0), 0) * 100) / 100);

  readonly chosen = computed(() => this.lines().filter((l) => (this.qty()[l.id] ?? 0) > 0));
  readonly linesValid = computed(() => this.chosen().length > 0 && this.lines().every((l) => !this.qtyError(l)));

  /** What could be paid back once this return is credited: what is held, less what is still owed for. */
  readonly refundableAfter = computed(() => {
    const i = this.invoice();
    if (!i) return 0;
    return Math.max(0, (cents(i.paidAmount) - cents(i.netTotal - this.credit())) / 100);
  });

  hasUnsavedChanges(): boolean {
    return !this.saved && (this.chosen().length > 0 || this.form.dirty);
  }

  ngOnInit(): void {
    this.cashBank.options().subscribe({ next: (o) => this.accountOptions.set(o) });
    this.refundForm.controls.paymentMethod.valueChanges.subscribe((m) =>
      this.refundForm.patchValue({ accountId: defaultAccountId(this.accountOptions(), m) }),
    );

    this.salesService.get(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (i) => {
        this.invoice.set(i);
        this.refundForm.patchValue({ paymentMethod: i.paymentMethod, accountId: defaultAccountId(this.accountOptions(), i.paymentMethod) });
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
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
    const i = this.invoice();
    if (!i || !this.linesValid() || this.form.invalid) {
      this.form.markAllAsTouched();
      this.error.set(this.chosen().length === 0 ? 'Enter a quantity for at least one item.' : 'Check the quantities entered.');
      return;
    }
    const r = this.refundForm.getRawValue();
    if (this.refundNow() && (r.amount == null || r.amount <= 0 || cents(r.amount) > cents(this.refundableAfter()))) {
      this.error.set(`The refund must be more than zero and no more than ${INR(this.refundableAfter())}.`);
      return;
    }

    const v = this.form.getRawValue();
    this.saving.set(true);
    const body = {
        salesInvoiceId: i.id,
        returnDate: v.returnDate,
        reason: v.reason || null,
        lines: this.chosen().map((l) => ({ salesInvoiceLineId: l.id, productUnitId: this.unitFor(l)?.productUnitId ?? null, qty: this.qty()[l.id]! })),
        refund: this.refundNow() ? { amount: r.amount!, paymentMethod: r.paymentMethod, accountId: r.accountId || null } : null,
      };
    this.salesService
      .createReturn(body, this.submission.keyFor(body))
      .subscribe({
        next: (created) => {
          this.saving.set(false);
          this.saved = true;
          this.toast.show(`${created.returnNo} recorded — stock restored.`);
          this.router.navigate(['/sales/returns', created.id]);
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          const problem = err.error as ApiProblem | undefined;
          this.error.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Could not record this return.');
        },
      });
  }
}
