import type { PaymentMethod } from '../../purchases/purchases.models';

export type { PaymentMethod };
export type ExpenseStatus = 'Active' | 'Cancelled';

export interface ExpenseCategory {
  id: string;
  name: string;
  isActive: boolean;
}

export interface ExpenseListItem {
  id: string;
  expenseNo: string;
  expenseDate: string;
  categoryName: string;
  reference: string | null;
  paymentMethod: PaymentMethod;
  amount: number;
  currencyCode: string;
  baseAmount: number;
  status: ExpenseStatus;
}

export interface ExpenseDetail {
  id: string;
  expenseNo: string;
  expenseDate: string;
  categoryId: string;
  categoryName: string;
  currencyId: string;
  currencyCode: string;
  currencySymbol: string;
  currencyDecimalPrecision: number;
  amount: number;
  exchangeRateToBase: number;
  baseAmount: number;
  baseCurrencyCode: string;
  baseCurrencySymbol: string;
  baseCurrencyDecimalPrecision: number;
  paymentMethod: PaymentMethod;
  reference: string | null;
  notes: string | null;
  status: ExpenseStatus;
  createdByName: string;
  createdAt: string;
  updatedAt: string | null;
  cancelledAt: string | null;
  cancelledByName: string | null;
  cancellationReason: string | null;
}

export interface CreateExpenseRequest {
  categoryId: string;
  expenseDate: string;
  currencyId: string;
  amount: number;
  paymentMethod: PaymentMethod;
  accountId?: string | null;
  reference?: string | null;
  notes?: string | null;
}

export interface UpdateExpenseRequest {
  categoryId: string;
  reference?: string | null;
  notes?: string | null;
}
