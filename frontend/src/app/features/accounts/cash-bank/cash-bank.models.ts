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
}

export interface UpdateAccountRequest {
  name: string;
  reference?: string | null;
}

export interface TransferRequest {
  fromAccountId: string;
  toAccountId: string;
  amount: number;
  transferDate: string;
  notes?: string | null;
}
