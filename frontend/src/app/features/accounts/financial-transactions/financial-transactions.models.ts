import type { PaymentMethod } from '../../purchases/purchases.models';

export type FinancialTransactionType = 'CustomerPayment' | 'SupplierPayment' | 'ExpensePayment' | 'OpeningBalance' | 'Transfer' | 'CustomerRefund' | 'SupplierRefund' | 'PaymentReversal';
export type MoneyDirection = 'In' | 'Out';
export type FinancialTransactionStatus = 'Active' | 'Cancelled';
export type RelatedType = 'Purchase' | 'Sale' | 'Expense';

export interface FinancialTransaction {
  id: string;
  transactionDate: string;
  type: FinancialTransactionType;
  direction: MoneyDirection;
  status: FinancialTransactionStatus;
  party: string;
  relatedType: RelatedType | null;
  relatedId: string | null;
  relatedNo: string | null;
  paymentMethod: PaymentMethod;
  amount: number;
  currencyCode: string;
  exchangeRateToBase: number;
  baseAmount: number;
  createdAt: string;
  accountId: string | null;
  accountName: string | null;
  isReconciled: boolean;
  notes: string | null;
}

export interface FinancialTransactionList {
  items: FinancialTransaction[];
  total: number;
  page: number;
  pageSize: number;
  moneyIn: number;
  moneyOut: number;
  baseCurrencyCode: string;
  baseCurrencySymbol: string;
}
