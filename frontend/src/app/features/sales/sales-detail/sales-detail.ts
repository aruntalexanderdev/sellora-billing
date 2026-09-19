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
import type { PaymentMethod, SalesInvoiceDetail as SalesInvoiceDetailModel } from '../sales.models';
import type { ApiProblem } from '../../../core/auth/auth.models';

const INR = (n: number) => '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-sales-detail',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, UiBadge],
  templateUrl: './sales-detail.html',
  styleUrl: '../../purchases/purchase-detail/purchase-detail.scss',
})
export class SalesDetail implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly salesService = inject(SalesService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

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
  });
  private readonly payAmount = toSignal(this.payForm.controls.amount.valueChanges, { initialValue: null as number | null });
  readonly dueAfter = computed(() => Math.max(0, (this.invoice()?.dueAmount ?? 0) - (this.payAmount() ?? 0)));

  ngOnInit(): void {
    this.load();
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
    this.payForm.reset({ amount: null, paymentMethod: this.invoice()!.paymentMethod });
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
    this.salesService.addPayment(i.id, { amount: v.amount!, paymentMethod: v.paymentMethod, expectedDue: i.dueAmount }).subscribe({
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
