import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../shared/ui/button/button';
import { UiBadge } from '../../../shared/ui/badge/badge';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { AuthService } from '../../../core/auth/auth.service';
import { SalesService } from '../sales.service';
import { formatQuantity } from '../../../shared/units/unit-conversion';
import type { PaymentMethod, SalesInvoiceDetail as SalesInvoiceDetailModel, SalesPayment } from '../sales.models';
import type { ApiProblem } from '../../../core/auth/auth.models';
import { CashBankService, defaultAccountId } from '../../accounts/cash-bank/cash-bank.service';
import type { AccountOption } from '../../accounts/cash-bank/cash-bank.models';


const INR = (n: number) => '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-sales-detail',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, UiBadge],
  templateUrl: './sales-detail.html',
  styleUrl: '../../purchases/purchase-detail/purchase-detail.scss',
})
export class SalesDetail implements OnInit {
  readonly formatQuantity = formatQuantity;
  private readonly fb = inject(FormBuilder);
  private readonly salesService = inject(SalesService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly cashBank = inject(CashBankService);
  readonly accountOptions = signal<AccountOption[]>([]);

  readonly INR = INR;

  readonly invoice = signal<SalesInvoiceDetailModel | null>(null);
  readonly loading = signal(true);

  readonly canCancel = computed(() => this.auth.hasPermission('Sales.Cancel'));
  readonly paymentMethods: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'];

  // ---- Receive payment (against the outstanding due) ----
  readonly paying = signal(false);
  readonly paySaving = signal(false);
  readonly payError = signal<string | null>(null);
  private payRequested = this.route.snapshot.queryParamMap.get('pay') !== null;

  readonly canReceive = computed(() => {
    const i = this.invoice();
    return this.auth.hasPermission('Sales.Receive') && !!i && i.status === 'Completed' && i.dueAmount > 0;
  });

  // Whole paise only: a payment that rounds to zero is not a payment.
  private readonly positiveAmount = (c: AbstractControl): ValidationErrors | null =>
    c.value == null || c.value === '' || Math.round(Number(c.value) * 100) > 0 ? null : { positive: true };
  private readonly withinDue = (c: AbstractControl): ValidationErrors | null =>
    c.value == null || c.value === '' || Math.round(Number(c.value) * 100) <= Math.round((this.invoice()?.dueAmount ?? 0) * 100)
      ? null
      : { exceedsDue: true };

  readonly payForm = this.fb.nonNullable.group({
    amount: [null as number | null, [Validators.required, this.positiveAmount, this.withinDue]],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
    accountId: [''],
  });
  private readonly payAmount = toSignal(this.payForm.controls.amount.valueChanges, { initialValue: null as number | null });
  readonly dueAfter = computed(() => Math.max(0, (this.invoice()?.dueAmount ?? 0) - (this.payAmount() ?? 0)));

  // ---- Return items / refund / reverse ----
  readonly canReturn = computed(() => {
    const i = this.invoice();
    return this.auth.hasPermission('Sales.Return') && !!i && i.status === 'Completed' && i.lines.some((l) => l.returnableBaseQty > 0);
  });
  readonly canRefund = computed(() => this.auth.hasPermission('Sales.Refund') && (this.invoice()?.refundableAmount ?? 0) > 0);
  readonly canReverse = (p: SalesPayment) => this.auth.hasPermission('Sales.Refund') && p.kind === 'Payment' && !p.isReversed;

  readonly refunding = signal(false);
  readonly refundSaving = signal(false);
  readonly refundError = signal<string | null>(null);

  private readonly withinRefundable = (c: AbstractControl): ValidationErrors | null =>
    c.value == null || c.value === '' || Math.round(Number(c.value) * 100) <= Math.round((this.invoice()?.refundableAmount ?? 0) * 100)
      ? null
      : { exceedsRefundable: true };

  readonly refundForm = this.fb.nonNullable.group({
    amount: [null as number | null, [Validators.required, this.positiveAmount, this.withinRefundable]],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
    accountId: [''],
    reason: [''],
    salesReturnId: [''],
  });

  readonly reversing = signal<SalesPayment | null>(null);
  readonly reverseSaving = signal(false);
  readonly reverseError = signal<string | null>(null);
  readonly reverseReason = this.fb.nonNullable.control('');

  goReturn(): void {
    this.router.navigate(['/sales', this.invoice()!.id, 'return']);
  }

  private loadAccounts(): void {
    this.cashBank.options().subscribe({ next: (o) => this.accountOptions.set(o) });
  }

  openRefund(): void {
    if (!this.canRefund()) return;
    this.refundError.set(null);
    this.reversing.set(null);
    this.paying.set(false);
    this.loadAccounts();
    // Money goes back the way it came in, so start from the invoice's last payment method.
    this.refundForm.reset({
      amount: this.invoice()!.refundableAmount, paymentMethod: this.invoice()!.paymentMethod,
      accountId: defaultAccountId(this.accountOptions(), this.invoice()!.paymentMethod), reason: '', salesReturnId: '',
    });
    this.refunding.set(true);
  }

  submitRefund(): void {
    if (this.refundSaving()) return;
    this.refundError.set(null);
    this.refundForm.controls.amount.updateValueAndValidity();
    if (this.refundForm.invalid) {
      this.refundForm.markAllAsTouched();
      return;
    }

    const i = this.invoice()!;
    const v = this.refundForm.getRawValue();
    this.refundSaving.set(true);
    this.salesService
      .addRefund(i.id, {
        amount: v.amount!, paymentMethod: v.paymentMethod, expectedRefundable: i.refundableAmount,
        accountId: v.accountId || null, reason: v.reason || null, salesReturnId: v.salesReturnId || null,
      })
      .subscribe({
        next: (updated) => {
          this.refundSaving.set(false);
          this.invoice.set(updated);
          this.refunding.set(false);
          this.toast.show('Refund recorded.');
        },
        error: (err: HttpErrorResponse) => {
          this.refundSaving.set(false);
          const problem = err.error as ApiProblem | undefined;
          this.refundError.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Could not record this refund.');
        },
      });
  }

  startReverse(p: SalesPayment): void {
    this.reverseError.set(null);
    this.reverseReason.reset('');
    this.refunding.set(false);
    this.paying.set(false);
    this.reversing.set(p);
  }

  submitReverse(): void {
    const p = this.reversing();
    if (!p || this.reverseSaving()) return;
    this.reverseError.set(null);
    this.reverseSaving.set(true);
    this.salesService.reversePayment(this.invoice()!.id, p.id, this.reverseReason.value || null).subscribe({
      next: (updated) => {
        this.reverseSaving.set(false);
        this.invoice.set(updated);
        this.reversing.set(null);
        this.toast.show('Payment reversed.');
      },
      error: (err: HttpErrorResponse) => {
        this.reverseSaving.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.reverseError.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Could not reverse this payment.');
      },
    });
  }

  ngOnInit(): void {
    this.load();
    this.refundForm.controls.paymentMethod.valueChanges.subscribe((m) =>
      this.refundForm.patchValue({ accountId: defaultAccountId(this.accountOptions(), m) }),
    );
    this.payForm.controls.paymentMethod.valueChanges.subscribe((m) =>
      this.payForm.patchValue({ accountId: defaultAccountId(this.accountOptions(), m) }),
    );
  }

  private load(): void {
    const id = this.route.snapshot.paramMap.get('id')!;
    this.loading.set(true);
    this.salesService.get(id).subscribe({
      next: (i) => {
        this.invoice.set(i);
        this.loading.set(false);

        // Arriving from the list's Receive payment action opens the payment panel straight away.
        if (this.payRequested) {
          this.payRequested = false;
          if (this.canReceive()) this.openPay();
        }
      },
      error: () => this.loading.set(false),
    });
  }

  openPay(): void {
    if (!this.canReceive()) return;
    this.payError.set(null);
    this.refunding.set(false);
    this.reversing.set(null);
    const method = this.invoice()!.paymentMethod;
    // Loaded when the panel opens, since only people who can receive payments ever need the list.
    this.cashBank.options().subscribe({
      next: (o) => {
        this.accountOptions.set(o);
        this.payForm.patchValue({ accountId: defaultAccountId(o, this.payForm.controls.paymentMethod.value) });
      },
    });
    this.payForm.reset({ amount: null, paymentMethod: method, accountId: defaultAccountId(this.accountOptions(), method) });
    this.paying.set(true);
  }

  closePay(): void {
    this.paying.set(false);
    this.payError.set(null);
    this.router.navigate([], { queryParams: { pay: null }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  submitPay(): void {
    // A second click while the first request is in flight must not send a second payment.
    if (this.paySaving()) return;
    this.payError.set(null);
    this.payForm.controls.amount.updateValueAndValidity();
    if (this.payForm.invalid) {
      this.payForm.markAllAsTouched();
      return;
    }

    const i = this.invoice()!;
    const v = this.payForm.getRawValue();
    this.paySaving.set(true);
    this.salesService.addPayment(i.id, { amount: v.amount!, paymentMethod: v.paymentMethod, expectedDue: i.dueAmount, accountId: v.accountId || null }).subscribe({
      next: (updated) => {
        this.paySaving.set(false);
        this.invoice.set(updated);
        this.toast.show(updated.dueAmount > 0 ? `Payment recorded · ${INR(updated.dueAmount)} still due.` : `${updated.invoiceNo} is fully paid.`);
        this.closePay();
      },
      error: (err: HttpErrorResponse) => {
        this.paySaving.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.payError.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Could not record this payment.');
      },
    });
  }

  print(): void {
    window.print();
  }

  cancelSale(): void {
    const i = this.invoice()!;
    this.confirm
      .ask({
        title: `Cancel ${i.invoiceNo}?`,
        body: 'This reverses the stock this sale removed, restoring the exact batches it drew from.',
        confirmLabel: 'Cancel sale',
        destructive: true,
      })
      .then((confirmed) => {
        if (!confirmed) return;
        this.salesService.cancel(i.id).subscribe({
          next: () => {
            this.toast.show(`${i.invoiceNo} cancelled — stock restored.`);
            this.load();
          },
          error: (err: HttpErrorResponse) => {
            const problem = err.error as ApiProblem | undefined;
            this.toast.show(problem?.title ?? 'Could not cancel this sale.');
          },
        });
      });
  }

  goBack(): void {
    this.router.navigateByUrl('/sales');
  }
}
