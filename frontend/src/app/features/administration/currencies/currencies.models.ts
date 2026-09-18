export interface CurrencyItem {
  id: string;
  code: string;
  name: string;
  symbol: string;
  decimalPrecision: number;
  isActive: boolean;
  isBase: boolean;
}

export interface UpsertCurrencyRequest {
  code: string;
  name: string;
  symbol: string;
  decimalPrecision: number;
}
