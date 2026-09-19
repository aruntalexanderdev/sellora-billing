import type { PaymentMethod } from '../../purchases/purchases.models';
import type { PaymentStatus } from '../../purchases/purchases.models';
import type { ExpenseStatus } from '../expenses/expenses.models';
import type { AccountKind } from '../cash-bank/cash-bank.models';

export type { PaymentMethod, PaymentStatus };
export type PaymentEntryKind = 'Payment' | 'Refund' | 'Reversal';

/** Every Accounts report is presented in the business base currency. */
export interface BaseCurrency {
  code: string;
  symbol: string;
  decimalPrecision: number;
}

export interface PaymentReportRow {
  id: string;
  paymentDate: string;
  kind: PaymentEntryKind;
  party: string;
  documentId: string;
  documentNo: string;
  paymentMethod: PaymentMethod;
  amount: number;
  currencyCode: string;
  exchangeRateToBase: number;
  baseAmount: number;
  documentPaymentStatus: PaymentStatus;
  accountName: string | null;
  reason: string | null;
}

export interface PaymentReport {
  items: PaymentReportRow[];
  total: number;
  page: number;
  pageSize: number;
  payments: number;
  refunds: number;
  reversals: number;
  net: number;
  baseCurrency: BaseCurrency;
}

export interface OutstandingRow {
  partyId: string;
  partyName: string;
  documentCount: number;
  total: number;
  returned: number;
  net: number;
  paid: number;
  outstanding: number;
  creditBalance: number;
  unpaidCount: number;
  partialCount: number;
  paidCount: number;
}

export interface OutstandingReport {
  items: OutstandingRow[];
  total: number;
  page: number;
  pageSize: number;
  totalNet: number;
  totalPaid: number;
  totalOutstanding: number;
  totalCredit: number;
  baseCurrency: BaseCurrency;
}

export interface ExpenseReportRow {
  id: string;
  expenseNo: string;
  expenseDate: string;
  category: string;
  reference: string | null;
  paymentMethod: PaymentMethod;
  amount: number;
  currencyCode: string;
  exchangeRateToBase: number;
  baseAmount: number;
  status: ExpenseStatus;
  accountName: string | null;
}

export interface ExpenseReport {
  items: ExpenseReportRow[];
  total: number;
  page: number;
  pageSize: number;
  totalBase: number;
  byCategory: { category: string; count: number; baseTotal: number }[];
  baseCurrency: BaseCurrency;
}

export interface CashFlowPeriod {
  period: string;
  moneyIn: number;
  moneyOut: number;
  net: number;
  received: number;
  paidToSuppliers: number;
  expenses: number;
  refunds: number;
}

export interface CashFlowReport {
  dateFrom: string | null;
  dateTo: string | null;
  receivedFromCustomers: number;
  paidToSuppliers: number;
  expenses: number;
  customerRefunds: number;
  supplierRefunds: number;
  refunds: number;
  reversalsIn: number;
  reversalsOut: number;
  transfersIn: number;
  transfersOut: number;
  moneyIn: number;
  moneyOut: number;
  net: number;
  months: CashFlowPeriod[];
  baseCurrency: BaseCurrency;
}

export interface CashBankReportRow {
  accountId: string;
  accountName: string;
  kind: AccountKind;
  isActive: boolean;
  opening: number;
  moneyIn: number;
  moneyOut: number;
  transfersIn: number;
  transfersOut: number;
  closing: number;
  unreconciledCount: number;
}

export interface CashBankReport {
  dateFrom: string | null;
  dateTo: string | null;
  items: CashBankReportRow[];
  opening: number;
  moneyIn: number;
  moneyOut: number;
  transfersIn: number;
  transfersOut: number;
  closing: number;
  baseCurrency: BaseCurrency;
}

export interface PaymentMethodSummaryRow {
  paymentMethod: PaymentMethod;
  transactions: number;
  received: number;
  paid: number;
  net: number;
}

export interface PaymentMethodSummary {
  dateFrom: string | null;
  dateTo: string | null;
  items: PaymentMethodSummaryRow[];
  totalReceived: number;
  totalPaid: number;
  totalNet: number;
  baseCurrency: BaseCurrency;
}
