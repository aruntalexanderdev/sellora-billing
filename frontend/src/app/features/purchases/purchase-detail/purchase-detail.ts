import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../shared/ui/button/button';
import { UiBadge } from '../../../shared/ui/badge/badge';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { AuthService } from '../../../core/auth/auth.service';
import { PurchasesService } from '../purchases.service';
import type { PurchaseDetail as PurchaseDetailModel, PaymentMethod } from '../purchases.models';
import type { ApiProblem } from '../../../core/auth/auth.models';

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
  readonly canCancel = computed(() => this.auth.hasPermission('Purchase.Cancel'));

  readonly headerForm = this.fb.nonNullable.group({
    supplierInvoiceNumber: [''],
    notes: [''],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
    paidAmount: [0, [Validators.required, Validators.min(0)]],
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
          paymentMethod: p.paymentMethod,
          paidAmount: p.paidAmount,
        });
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  startEdit(): void {
    this.editing.set(true);
  }

  cancelEdit(): void {
    const p = this.purchase()!;
    this.headerForm.patchValue({
      supplierInvoiceNumber: p.supplierInvoiceNumber ?? '',
      notes: p.notes ?? '',
      paymentMethod: p.paymentMethod,
      paidAmount: p.paidAmount,
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
