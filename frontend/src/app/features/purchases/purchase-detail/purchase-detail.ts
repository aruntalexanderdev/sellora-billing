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
import { PurchasesService } from '../purchases.service';
import type { PurchaseDetail as PurchaseDetailModel, PaymentMethod } from '../purchases.models';
import type { ApiProblem } from '../../../core/auth/auth.models';

const roundTo = (n: number, places: number) => {
  const f = 10 ** places;
  return (Math.sign(n) * Math.round(Math.abs(n) * f + Number.EPSILON)) / f;
};

const FORMAT = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-purchase-detail',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, UiBadge],
  templateUrl: './purchase-detail.html',
  styleUrl: './purchase-detail.scss',
})
export class PurchaseDetail implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly purchasesService = inject(PurchasesService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  // Every stored amount on a purchase is in the business base currency; only OriginalGrandTotal is in
  // the purchase's own transaction currency.
  readonly base = (n: number) => (this.purchase()?.baseCurrencySymbol ?? '₹') + FORMAT(n);
  readonly original = (n: number) => (this.purchase()?.currencySymbol ?? '') + FORMAT(n);
  readonly paymentMethods: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'];

  readonly purchase = signal<PurchaseDetailModel | null>(null);
  readonly loading = signal(true);
  readonly editing = signal(false);
  readonly saving = signal(false);

  readonly canEdit = computed(() => this.auth.hasPermission('Purchase.Edit'));
  readonly canPayPermission = computed(() => this.auth.hasPermission('Purchase.Pay'));
  readonly canCancel = computed(() => this.auth.hasPermission('Purchase.Cancel'));

  // ---- Pay (an additional payment against the outstanding due) ----
  readonly paying = signal(false);
  readonly paySaving = signal(false);
  readonly payError = signal<string | null>(null);
  private payRequested = this.route.snapshot.queryParamMap.get('pay') !== null;

  readonly canPay = computed(() => {
    const p = this.purchase();
    return this.canPayPermission() && !!p && p.status === 'Completed' && p.originalDueAmount > 0;
  });

  private readonly precision = () => this.purchase()?.currencyDecimalPrecision ?? 2;

  // Must be more than zero once rounded to the purchase currency's precision, and no more than the due.
  private readonly positiveAmount = (c: AbstractControl): ValidationErrors | null =>
    c.value == null || c.value === '' || roundTo(Number(c.value), this.precision()) > 0 ? null : { positive: true };
  private readonly withinDue = (c: AbstractControl): ValidationErrors | null => {
    const due = this.purchase()?.originalDueAmount ?? 0;
    return c.value == null || c.value === '' || roundTo(Number(c.value), this.precision()) <= due ? null : { exceedsDue: true };
  };

  readonly payForm = this.fb.nonNullable.group({
    amount: [null as number | null, [Validators.required, this.positiveAmount, this.withinDue]],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
  });
  private readonly payAmount = toSignal(this.payForm.controls.amount.valueChanges, { initialValue: null as number | null });

  readonly payStep = computed(() => String(1 / 10 ** (this.purchase()?.currencyDecimalPrecision ?? 2)));

  // Previews what the server will record, using the same steps: the amount is added to what was already
  // paid, a payment that clears the whole invoice books the exact base total, otherwise the running paid
  // amount is converted at the purchase's captured rate. The response from the server is authoritative.
  readonly afterPayment = computed(() => {
    const p = this.purchase();
    if (!p) return null;
    const amount = roundTo(this.payAmount() ?? 0, p.currencyDecimalPrecision);
    const paidOriginal = p.originalPaidAmount + amount;
    const foreign = p.exchangeRateToBase !== 1;
    let paidBase = paidOriginal;
    if (foreign) {
      paidBase = paidOriginal >= p.originalGrandTotal ? p.grandTotal : roundTo(paidOriginal * p.exchangeRateToBase, p.baseCurrencyDecimalPrecision);
    }
    return {
      dueOriginal: Math.max(0, p.originalGrandTotal - paidOriginal),
      dueBase: Math.max(0, p.grandTotal - paidBase),
    };
  });

  readonly headerForm = this.fb.nonNullable.group({
    supplierInvoiceNumber: [''],
    notes: [''],
  });

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    const id = this.route.snapshot.paramMap.get('id')!;
    this.loading.set(true);
    this.purchasesService.get(id).subscribe({
      next: (p) => {
        this.purchase.set(p);
        this.headerForm.patchValue({
          supplierInvoiceNumber: p.supplierInvoiceNumber ?? '',
          notes: p.notes ?? '',
        });
        this.loading.set(false);

        // Arriving from the list's Pay action opens the payment panel straight away.
        if (this.payRequested) {
          this.payRequested = false;
          if (this.canPay()) this.openPay();
        }
      },
      error: () => this.loading.set(false),
    });
  }

  openPay(): void {
    if (!this.canPay()) return;
    this.payError.set(null);
    this.payForm.reset({ amount: null, paymentMethod: this.purchase()!.paymentMethod });
    this.editing.set(false);
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

    const p = this.purchase()!;
    const v = this.payForm.getRawValue();
    this.paySaving.set(true);
    this.purchasesService
      .addPayment(p.id, { amount: v.amount!, paymentMethod: v.paymentMethod, expectedDue: p.originalDueAmount })
      .subscribe({
        next: (updated) => {
          this.paySaving.set(false);
          this.purchase.set(updated);
          this.toast.show(
            updated.originalDueAmount > 0
              ? `Payment recorded · ${this.original(updated.originalDueAmount)} still due.`
              : `${updated.purchaseNo} is fully paid.`,
          );
          this.closePay();
        },
        error: (err: HttpErrorResponse) => {
          this.paySaving.set(false);
          const problem = err.error as ApiProblem | undefined;
          this.payError.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Could not record this payment.');
        },
      });
  }

  startEdit(): void {
    this.paying.set(false);
    this.editing.set(true);
  }

  cancelEdit(): void {
    const p = this.purchase()!;
    this.headerForm.patchValue({
      supplierInvoiceNumber: p.supplierInvoiceNumber ?? '',
      notes: p.notes ?? '',
    });
    this.editing.set(false);
  }

  saveEdit(): void {
    if (this.headerForm.invalid) {
      this.headerForm.markAllAsTouched();
      return;
    }
    this.saving.set(true);
    this.purchasesService.updateHeader(this.purchase()!.id, this.headerForm.getRawValue()).subscribe({
      next: (p) => {
        this.purchase.set(p);
        this.saving.set(false);
        this.editing.set(false);
        this.toast.show('Purchase updated.');
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.toast.show(problem?.title ?? 'Could not update this purchase.');
      },
    });
  }

  print(): void {
    window.print();
  }

  cancelPurchase(): void {
    const p = this.purchase()!;
    this.confirm
      .ask({
        title: `Cancel ${p.purchaseNo}?`,
        body: 'This reverses the stock this purchase added. It can only be cancelled if none of it has been sold yet.',
        confirmLabel: 'Cancel purchase',
        destructive: true,
      })
      .then((confirmed) => {
        if (!confirmed) return;
        this.purchasesService.cancel(p.id).subscribe({
          next: () => {
            this.toast.show(`${p.purchaseNo} cancelled — stock reversed.`);
            this.load();
          },
          error: (err: HttpErrorResponse) => {
            const problem = err.error as ApiProblem | undefined;
            this.toast.show(problem?.title ?? 'Could not cancel this purchase.');
          },
        });
      });
  }

  goBack(): void {
    this.router.navigateByUrl('/purchases');
  }
}
