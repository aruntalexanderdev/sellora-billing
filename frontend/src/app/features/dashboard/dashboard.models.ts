import type { StockStatus } from '../reports/reports.models';

export interface DashboardSalesSummary {
  amount: number;
  transactionCount: number;
}

export interface DashboardPurchaseSummary {
  amount: number;
  transactionCount: number;
}

export interface DashboardLowStockItem {
  productId: string;
  name: string;
  baseUnitLabel: string;
  quantityOnHand: number;
  reorderLevel: number;
  status: StockStatus;
}

export interface DashboardRecentSale {
  id: string;
  invoiceNo: string;
  customerName: string;
  grandTotal: number;
  status: 'Completed' | 'Cancelled';
  createdAt: string;
}

export interface DashboardRecentPurchase {
  id: string;
  purchaseNo: string;
  supplierName: string;
  grandTotal: number;
  status: 'Completed' | 'Cancelled';
  createdAt: string;
}

export interface Dashboard {
  todaysSales: DashboardSalesSummary | null;
  todaysPurchases: DashboardPurchaseSummary | null;
  currentStockValue: number | null;
  outstandingCustomerDue: number | null;
  lowStockItems: DashboardLowStockItem[] | null;
  recentSales: DashboardRecentSale[] | null;
  recentPurchases: DashboardRecentPurchase[] | null;
}
