export type AccountKind = 'Cash' | 'Bank';

/** Tooltip shown instead of a hidden (null) balance. */
export const HIDDEN_BALANCE_HINT = 'Company account – balance visible to company-level users';

export interface FinancialAccount {
  id: string;
  name: string;
  kind: AccountKind;
  reference: string | null;
  isActive: boolean;
  /** Null = company-level account and the reader lacks the company-level accounts permission (FI-D12): show "—", never 0. */
  balance: number | null;
  reconciledBalance: number | null;
  unreconciledCount: number;
  /** How far below zero the account may go (bank only; 0 for cash). */
  overdraftLimit: number;
}

export interface FinancialAccountList {
  items: FinancialAccount[];
  cashBalance: number;
  bankBalance: number;
  baseCurrencyCode: string;
  baseCurrencySymbol: string;
  baseCurrencyDecimalPrecision: number;
}

/** What a payment form needs to offer an account. */
export interface AccountOption {
  id: string;
  name: string;
  kind: AccountKind;
  /** Null = company-level account (any branch); otherwise only documents of that branch. */
  branchId?: string | null;
}

export interface CreateAccountRequest {
  name: string;
  kind: AccountKind;
  reference?: string | null;
  openingBalance: number;
  openingDate?: string | null;
  /** Bank only; cash is always 0. */
  overdraftLimit: number;
}

export interface UpdateAccountRequest {
  name: string;
  reference?: string | null;
  overdraftLimit: number;
}

export interface TransferRequest {
  fromAccountId: string;
  toAccountId: string;
  amount: number;
  transferDate: string;
  notes?: string | null;
}

export type FundingSource = 'OwnerCapital' | 'Loan' | 'CashDeposit' | 'CustomerReceipt' | 'Other';

export const FUNDING_SOURCES: { value: FundingSource; label: string; hint: string }[] = [
  { value: 'OwnerCapital', label: 'Owner capital', hint: 'Money the owner puts into the business.' },
  { value: 'Loan', label: 'Loan', hint: 'Money borrowed; record the lender and loan reference.' },
  {
    value: 'CashDeposit',
    label: 'Cash deposit',
    hint: 'Cash from outside the tracked accounts. To move cash between your own accounts, use Transfer.',
  },
  { value: 'CustomerReceipt', label: 'Customer receipt', hint: 'Unallocated, not linked to an invoice.' },
  { value: 'Other', label: 'Other', hint: 'Approved funding - a reason is required.' },
];

export const FUNDING_SOURCE_LABEL: Record<string, string> = Object.fromEntries(FUNDING_SOURCES.map((s) => [s.value, s.label]));

export interface FundingRequest {
  source: FundingSource;
  amount: number;
  fundingDate: string;
  sourceReference?: string | null;
  counterparty?: string | null;
  customerId?: string | null;
  reason?: string | null;
  notes?: string | null;
}

export interface FundingResult {
  transactionId: string;
}

export interface ReverseRequest {
  reason: string;
}

export interface ReverseResult {
  reversalTransactionIds: string[];
}

export interface StatementRow {
  transactionId: string;
  date: string;
  type: string;
  fundingSource: string | null;
  description: string;
  reference: string | null;
  moneyIn: number;
  moneyOut: number;
  runningBalance: number;
  reconciled: boolean;
  isReversal: boolean;
  reversedByTransactionId: string | null;
}

export interface AccountStatement {
  accountId: string;
  from: string;
  to: string;
  openingBalance: number;
  closingBalance: number;
  totalIn: number;
  totalOut: number;
  rows: StatementRow[];
}

/** Balance the account can pay out: book balance plus its overdraft allowance. Null when the balance is hidden. */
export function availableBalance(a: Pick<FinancialAccount, 'balance' | 'overdraftLimit'>): number | null {
  return a.balance === null ? null : a.balance + (a.overdraftLimit ?? 0);
}
