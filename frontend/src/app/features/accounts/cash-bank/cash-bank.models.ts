export type AccountKind = 'Cash' | 'Bank';

export interface FinancialAccount {
  id: string;
  name: string;
  kind: AccountKind;
  reference: string | null;
  isActive: boolean;
  balance: number;
  reconciledBalance: number;
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
