import { Routes } from '@angular/router';
import { permissionGuard } from '../../core/auth/auth.guard';
import { unsavedChangesGuard } from '../../core/unsaved-changes.guard';

export const ACCOUNTS_ROUTES: Routes = [
  {
    path: 'expenses',
    loadComponent: () => import('./expenses/expense-list/expense-list').then((m) => m.ExpenseList),
    canActivate: [permissionGuard('Expenses.View')],
    data: { group: 'Accounts', page: 'Expenses' },
  },
  {
    path: 'expenses/new',
    loadComponent: () => import('./expenses/expense-form/expense-form').then((m) => m.ExpenseForm),
    canActivate: [permissionGuard('Expenses.Create')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Accounts', page: 'New expense' },
  },
  {
    path: 'expenses/categories',
    loadComponent: () => import('./expenses/expense-categories/expense-categories').then((m) => m.ExpenseCategories),
    canActivate: [permissionGuard('Expenses.Categories')],
    data: { group: 'Accounts', page: 'Expense categories' },
  },
  {
    path: 'expenses/:id',
    loadComponent: () => import('./expenses/expense-detail/expense-detail').then((m) => m.ExpenseDetail),
    canActivate: [permissionGuard('Expenses.View')],
    data: { group: 'Accounts', page: 'Expense details' },
  },
  {
    path: 'cash-bank',
    loadComponent: () => import('./cash-bank/account-list/account-list').then((m) => m.AccountList),
    canActivate: [permissionGuard('CashBank.View')],
    data: { group: 'Accounts', page: 'Cash & Bank' },
  },
  {
    path: 'cash-bank/new',
    loadComponent: () => import('./cash-bank/account-form/account-form').then((m) => m.AccountForm),
    canActivate: [permissionGuard('CashBank.Manage')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Accounts', page: 'New account' },
  },
  {
    path: 'cash-bank/transfer',
    loadComponent: () => import('./cash-bank/transfer-form/transfer-form').then((m) => m.TransferForm),
    canActivate: [permissionGuard('CashBank.Transfer')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Accounts', page: 'Transfer money' },
  },
  {
    path: 'cash-bank/:id/edit',
    loadComponent: () => import('./cash-bank/account-form/account-form').then((m) => m.AccountForm),
    canActivate: [permissionGuard('CashBank.Manage')],
    canDeactivate: [unsavedChangesGuard],
    data: { group: 'Accounts', page: 'Edit account' },
  },
  {
    path: 'cash-bank/:id',
    loadComponent: () => import('./cash-bank/account-detail/account-detail').then((m) => m.AccountDetail),
    canActivate: [permissionGuard('CashBank.View')],
    data: { group: 'Accounts', page: 'Account history' },
  },
  {
    path: 'reports/purchase-payments',
    loadComponent: () => import('./reports/payment-report/payment-report').then((m) => m.PaymentReport),
    canActivate: [permissionGuard('Reports.Accounts')],
    data: { group: 'Accounts Reports', page: 'Purchase Payment Report', side: 'purchase' },
  },
  {
    path: 'reports/sales-payments',
    loadComponent: () => import('./reports/payment-report/payment-report').then((m) => m.PaymentReport),
    canActivate: [permissionGuard('Reports.Accounts')],
    data: { group: 'Accounts Reports', page: 'Sales Payment Report', side: 'sales' },
  },
  {
    path: 'reports/supplier-outstanding',
    loadComponent: () => import('./reports/outstanding-report/outstanding-report').then((m) => m.OutstandingReport),
    canActivate: [permissionGuard('Reports.Accounts')],
    data: { group: 'Accounts Reports', page: 'Supplier Outstanding', side: 'supplier' },
  },
  {
    path: 'reports/customer-outstanding',
    loadComponent: () => import('./reports/outstanding-report/outstanding-report').then((m) => m.OutstandingReport),
    canActivate: [permissionGuard('Reports.Accounts')],
    data: { group: 'Accounts Reports', page: 'Customer Outstanding', side: 'customer' },
  },
  {
    path: 'reports/expenses',
    loadComponent: () => import('./reports/expense-report/expense-report').then((m) => m.ExpenseReport),
    canActivate: [permissionGuard('Reports.Accounts')],
    data: { group: 'Accounts Reports', page: 'Expense Report' },
  },
  {
    path: 'reports/cash-flow',
    loadComponent: () => import('./reports/cash-flow-report/cash-flow-report').then((m) => m.CashFlowReport),
    canActivate: [permissionGuard('Reports.Accounts')],
    data: { group: 'Accounts Reports', page: 'Cash Flow' },
  },
  {
    path: 'reports/cash-bank',
    loadComponent: () => import('./reports/cash-bank-report/cash-bank-report').then((m) => m.CashBankReport),
    canActivate: [permissionGuard('Reports.Accounts')],
    data: { group: 'Accounts Reports', page: 'Cash & Bank Report' },
  },
  {
    path: 'reports/payment-methods',
    loadComponent: () => import('./reports/payment-method-report/payment-method-report').then((m) => m.PaymentMethodReport),
    canActivate: [permissionGuard('Reports.Accounts')],
    data: { group: 'Accounts Reports', page: 'Payment Method Summary' },
  },
  {
    path: 'financial-transactions',
    loadComponent: () => import('./financial-transactions/financial-transaction-list/financial-transaction-list').then((m) => m.FinancialTransactionList),
    canActivate: [permissionGuard('FinancialTransactions.View')],
    data: { group: 'Accounts', page: 'Financial Transactions' },
  },
];
