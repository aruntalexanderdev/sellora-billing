import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { CashBankService } from '../cash-bank.service';
import { HIDDEN_BALANCE_HINT, type FinancialAccount, type FinancialAccountList } from '../cash-bank.models';

const money = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-account-list',
  standalone: true,
  imports: [RouterLink, UiButton, UiBadge],
  templateUrl: './account-list.html',
  styleUrl: './account-list.scss',
})
export class AccountList implements OnInit {
  private readonly service = inject(CashBankService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly auth = inject(AuthService);
  readonly router = inject(Router);

  readonly data = signal<FinancialAccountList | null>(null);
  readonly loading = signal(true);
  readonly failed = signal(false);

  readonly canManage = computed(() => this.auth.hasPermission('CashBank.Manage'));
  readonly canFund = computed(() => this.auth.hasPermission('CashBank.Fund'));
  readonly canTransfer = computed(() => this.auth.hasPermission('CashBank.Transfer'));
  readonly fmt = (n: number | null) => (n === null ? '—' : (this.data()?.baseCurrencySymbol ?? '₹') + money(n));
  readonly hiddenHint = HIDDEN_BALANCE_HINT;
  readonly total = computed(() => (this.data()?.cashBalance ?? 0) + (this.data()?.bankBalance ?? 0));

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.failed.set(false);
    this.service.list().subscribe({
      next: (d) => {
        this.data.set(d);
        this.loading.set(false);
      },
      error: () => {
        this.failed.set(true);
        this.loading.set(false);
      },
    });
  }

  open(a: FinancialAccount): void {
    this.router.navigate(['/accounts/cash-bank', a.id]);
  }

  toggle(a: FinancialAccount): void {
    const disabling = a.isActive;
    this.confirm
      .ask({
        title: disabling ? `Deactivate ${a.name}?` : `Activate ${a.name}?`,
        body: disabling
          ? 'It will no longer be offered for new payments or transfers. Its history and balance are kept.'
          : 'It will be offered for payments and transfers again.',
        confirmLabel: disabling ? 'Deactivate' : 'Activate',
        destructive: disabling,
      })
      .then((confirmed) => {
        if (!confirmed) return;
        this.service.setActive(a.id, !a.isActive).subscribe({
          next: () => {
            this.toast.show(`${a.name} ${disabling ? 'deactivated' : 'activated'}.`);
            this.load();
          },
          error: (err: HttpErrorResponse) => this.toast.show(err.error?.title ?? 'Could not update this account.'),
        });
      });
  }
}
