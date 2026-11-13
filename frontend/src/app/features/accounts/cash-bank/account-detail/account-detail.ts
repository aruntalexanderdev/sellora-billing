import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { CashBankService } from '../cash-bank.service';
import { FinancialTransactionsService } from '../../financial-transactions/financial-transactions.service';
import { FUNDING_SOURCE_LABEL, HIDDEN_BALANCE_HINT, type AccountStatement, type FinancialAccount } from '../cash-bank.models';
import { IdempotentSubmission } from '../../../../core/idempotency';
import type { FinancialTransaction, RelatedType } from '../../financial-transactions/financial-transactions.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';

const PAGE_SIZE = 50;
const money = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const cents = (n: number) => Math.round(n * 100);

const TYPE_LABEL: Record<string, string> = {
  CustomerPayment: 'Customer payment',
  SupplierPayment: 'Supplier payment',
  ExpensePayment: 'Expense paid',
  OpeningBalance: 'Opening balance',
  Transfer: 'Transfer',
  Funding: 'Funding',
  Reversal: 'Reversal',
  CustomerRefund: 'Customer refund',
  SupplierRefund: 'Supplier refund',
  PaymentReversal: 'Payment reversal',
};

const REVERSIBLE = new Set(['Funding', 'Transfer', 'OpeningBalance']);
const pad = (n: number) => String(n).padStart(2, '0');
const isoDate = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
/** First and last day of the current month, yyyy-MM-dd. */
export function currentMonthRange(now = new Date()): { from: string; to: string } {
  return { from: isoDate(new Date(now.getFullYear(), now.getMonth(), 1)), to: isoDate(new Date(now.getFullYear(), now.getMonth() + 1, 0)) };
}

const ROUTE: Record<RelatedType, string> = { Purchase: '/purchases', Sale: '/sales', Expense: '/accounts/expenses' };

@Component({
  selector: 'app-account-detail',
  standalone: true,
  imports: [FormsModule, RouterLink, UiButton, UiBadge],
  templateUrl: './account-detail.html',
  styleUrl: './account-detail.scss',
})
export class AccountDetail implements OnInit {
  private readonly service = inject(CashBankService);
  private readonly transactionsService = inject(FinancialTransactionsService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  readonly account = signal<FinancialAccount | null>(null);
  readonly transactions = signal<FinancialTransaction[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly symbol = signal('₹');
  readonly loading = signal(true);
  readonly loadingMore = signal(false);
  readonly working = signal(false);
  readonly error = signal<string | null>(null);

  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly statementBalance = signal<number | null>(null);

  readonly canFund = computed(() => this.auth.hasPermission('CashBank.Fund'));
  readonly canReverse = computed(() => this.auth.hasPermission('CashBank.Reverse'));
  readonly canTransfer = computed(() => this.auth.hasPermission('CashBank.Transfer'));
  readonly canReconcile = computed(() => this.auth.hasPermission('CashBank.Reconcile'));
  readonly hasMore = computed(() => this.transactions().length < this.total());
  readonly fmt = (n: number | null) => (n === null ? '—' : this.symbol() + money(n));
  readonly hiddenHint = HIDDEN_BALANCE_HINT;
  readonly label = (t: FinancialTransaction) => TYPE_LABEL[t.type] ?? t.type;

  // The statement balance is what the bank/cash count says; what should match it is the reconciled balance.
  readonly difference = computed(() => {
    const a = this.account();
    const s = this.statementBalance();
    return a && s !== null && a.reconciledBalance !== null ? (cents(s) - cents(a.reconciledBalance)) / 100 : null;
  });

  // Statement tab
  readonly tab = signal<'history' | 'statement'>('history');
  private readonly month = currentMonthRange();
  readonly from = signal(this.month.from);
  readonly to = signal(this.month.to);
  readonly statement = signal<AccountStatement | null>(null);
  readonly statementLoading = signal(false);
  readonly statementError = signal<string | null>(null);
  readonly fundingLabel = (s: string | null) => (s ? (FUNDING_SOURCE_LABEL[s] ?? s) : null);
  readonly typeLabel = (type: string) => TYPE_LABEL[type] ?? type;

  // Reverse panel
  readonly reversing = signal<FinancialTransaction | null>(null);
  readonly reverseReason = signal('');
  readonly reverseError = signal<string | null>(null);
  private readonly reverseSubmission = new IdempotentSubmission();

  private get id(): string {
    return this.route.snapshot.paramMap.get('id')!;
  }

  ngOnInit(): void {
    this.reload();
  }

  private reload(): void {
    this.loading.set(true);
    this.service.get(this.id).subscribe({
      next: (a) => this.account.set(a),
      error: () => this.loading.set(false),
    });
    this.loadTransactions(1);
  }

  private loadTransactions(page: number): void {
    (page === 1 ? this.loading : this.loadingMore).set(true);
    this.transactionsService.list({ accountId: this.id, page, pageSize: PAGE_SIZE }).subscribe({
      next: (res) => {
        this.symbol.set(res.baseCurrencySymbol);
        this.transactions.set(page === 1 ? res.items : [...this.transactions(), ...res.items]);
        this.total.set(res.total);
        this.page.set(page);
        this.loading.set(false);
        this.loadingMore.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadingMore.set(false);
      },
    });
  }

  loadMore(): void {
    if (!this.loadingMore()) this.loadTransactions(this.page() + 1);
  }

  signed(t: FinancialTransaction): string {
    return `${t.direction === 'In' ? '+' : '−'}${money(t.baseAmount)}`;
  }

  // A cancelled transaction no longer counts toward the balance, so it can't be matched to a statement.
  canSelect(t: FinancialTransaction): boolean {
    return this.canReconcile() && t.status === 'Active';
  }

  toggle(t: FinancialTransaction): void {
    const next = new Set(this.selected());
    if (!next.delete(t.id)) next.add(t.id);
    this.selected.set(next);
  }

  toggleAll(checked: boolean): void {
    this.selected.set(checked ? new Set(this.transactions().filter((t) => this.canSelect(t)).map((t) => t.id)) : new Set());
  }

  readonly selectedUnreconciled = computed(() => this.transactions().filter((t) => this.selected().has(t.id) && !t.isReconciled).length);
  readonly selectedReconciled = computed(() => this.transactions().filter((t) => this.selected().has(t.id) && t.isReconciled).length);

  open(t: FinancialTransaction): void {
    if (t.relatedType && t.relatedId) this.router.navigate([ROUTE[t.relatedType], t.relatedId]);
  }

  reconcile(mark: boolean): void {
    if (this.working()) return;
    const ids = this.transactions().filter((t) => this.selected().has(t.id) && t.isReconciled !== mark).map((t) => t.id);
    if (ids.length === 0) return;

    this.working.set(true);
    this.error.set(null);
    (mark ? this.service.reconcile(this.id, ids) : this.service.unreconcile(this.id, ids)).subscribe({
      next: () => {
        this.working.set(false);
        this.selected.set(new Set());
        this.toast.show(mark ? 'Marked as reconciled.' : 'Reconciliation cleared.');
        this.reload();
      },
      error: (err: HttpErrorResponse) => {
        this.working.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.error.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Could not update reconciliation.');
      },
    });
  }

  /** Funding, transfer and opening balance rows that are neither reconciled nor already reversed. */
  canReverseRow(t: FinancialTransaction): boolean {
    return this.canReverse() && REVERSIBLE.has(t.type) && t.status === 'Active' && !t.isReconciled && !t.reversedByTransactionId && !t.reversalOfTransactionId;
  }

  describe(t: FinancialTransaction): string {
    const parts = [this.fundingLabel(t.fundingSource), t.counterparty, t.sourceReference, t.reason, t.notes].filter((p): p is string => !!p);
    return parts.join(' · ');
  }

  selectTab(tab: 'history' | 'statement'): void {
    this.tab.set(tab);
    if (tab === 'statement' && !this.statement() && !this.statementLoading()) this.loadStatement();
  }

  setRange(from: string, to: string): void {
    this.from.set(from);
    this.to.set(to);
  }

  loadStatement(): void {
    if (!this.from() || !this.to() || this.from() > this.to()) {
      this.statementError.set('Choose a valid date range.');
      return;
    }
    this.statementLoading.set(true);
    this.statementError.set(null);
    this.service.statement(this.id, this.from(), this.to()).subscribe({
      next: (s) => {
        this.statement.set(s);
        this.statementLoading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.statement.set(null);
        this.statementLoading.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.statementError.set(
          err.status === 403
            ? 'The statement for this company-level account is only visible to company-level users.'
            : (Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Could not load the statement.'),
        );
      },
    });
  }

  startReverse(t: FinancialTransaction): void {
    this.reversing.set(t);
    this.reverseReason.set('');
    this.reverseError.set(null);
  }

  cancelReverse(): void {
    this.reversing.set(null);
  }

  confirmReverse(): void {
    const t = this.reversing();
    const reason = this.reverseReason().trim();
    if (!t || this.working()) return;
    if (!reason) {
      this.reverseError.set('A reason is required.');
      return;
    }
    this.working.set(true);
    this.reverseError.set(null);
    this.service.reverse(t.id, reason, this.reverseSubmission.keyFor({ id: t.id, reason })).subscribe({
      next: () => {
        this.reverseSubmission.succeeded();
        this.working.set(false);
        this.reversing.set(null);
        this.toast.show('Transaction reversed.');
        this.statement.set(null);
        this.reload();
        if (this.tab() === 'statement') this.loadStatement();
      },
      error: (err: HttpErrorResponse) => {
        this.reverseSubmission.failed(err);
        this.working.set(false);
        const problem = err.error as ApiProblem | undefined;
        const errors = problem?.errors ?? {};
        this.reverseError.set(errors['reason']?.[0] ?? errors['id']?.[0] ?? Object.values(errors)[0]?.[0] ?? problem?.title ?? 'Could not reverse this transaction.');
      },
    });
  }

  addFunds(): void {
    this.router.navigate(['/accounts/cash-bank', this.id, 'fund']);
  }

  transfer(): void {
    this.router.navigate(['/accounts/cash-bank/transfer'], { queryParams: { from: this.id } });
  }
}
