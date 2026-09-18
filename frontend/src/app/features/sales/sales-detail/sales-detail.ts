import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../shared/ui/button/button';
import { UiBadge } from '../../../shared/ui/badge/badge';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { AuthService } from '../../../core/auth/auth.service';
import { SalesService } from '../sales.service';
import type { SalesInvoiceDetail as SalesInvoiceDetailModel } from '../sales.models';
import type { ApiProblem } from '../../../core/auth/auth.models';

const INR = (n: number) => '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-sales-detail',
  standalone: true,
  imports: [RouterLink, UiButton, UiBadge],
  templateUrl: './sales-detail.html',
  styleUrl: '../../purchases/purchase-detail/purchase-detail.scss',
})
export class SalesDetail implements OnInit {
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
      },
      error: () => this.loading.set(false),
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
