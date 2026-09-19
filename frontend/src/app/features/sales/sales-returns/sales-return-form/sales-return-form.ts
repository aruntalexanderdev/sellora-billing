import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { SalesService } from '../../sales.service';
import { CashBankService, defaultAccountId } from '../../../accounts/cash-bank/cash-bank.service';
import type { AccountOption } from '../../../accounts/cash-bank/cash-bank.models';
import type { PaymentMethod, SalesInvoiceDetail } from '../../sales.models';
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

  /** Quantity being returned per invoice line, keyed by line id. */
  readonly qty = signal<Record<string, number | null>>({});

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

  readonly lines = computed(() => this.invoice()?.lines.filter((l) => l.returnableQty > 0) ?? []);

  /** An estimate of the credit — the server works the exact figure, taking the remainder on a final return. */
  readonly credit = computed(() => {
    let total = 0;
    for (const l of this.lines()) {
      const q = this.qty()[l.id] ?? 0;
      if (q > 0) total += (l.lineTotal * q) / l.qty;
    }
    return Math.round(total * 100) / 100;
  });

  readonly chosen = computed(() => this.lines().filter((l) => (this.qty()[l.id] ?? 0) > 0));
  readonly invalidLine = (id: string, max: number) => {
    const q = this.qty()[id];
    return q != null && (q < 0 || q > max);
  };
  readonly linesValid = computed(() => this.chosen().length > 0 && this.lines().every((l) => !this.invalidLine(l.id, l.returnableQty)));

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

  returnAll(): void {
    this.qty.set(Object.fromEntries(this.lines().map((l) => [l.id, l.returnableQty])));
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
    this.salesService
      .createReturn({
        salesInvoiceId: i.id,
        returnDate: v.returnDate,
        reason: v.reason || null,
        lines: this.chosen().map((l) => ({ salesInvoiceLineId: l.id, qty: this.qty()[l.id]! })),
        refund: this.refundNow() ? { amount: r.amount!, paymentMethod: r.paymentMethod, accountId: r.accountId || null } : null,
      })
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
