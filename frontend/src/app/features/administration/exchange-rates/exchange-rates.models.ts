export interface ExchangeRateItem {
  id: string;
  currencyId: string;
  currencyCode: string;
  currencySymbol: string;
  rateToBase: number;
  effectiveDate: string;
  createdAt: string;
}

export interface UpsertExchangeRateRequest {
  currencyId: string;
  rateToBase: number;
  effectiveDate: string;
}

export interface ResolvedExchangeRate {
  currencyId: string;
  asOfDate: string;
  rateToBase: number;
  baseCurrencyCode: string;
  baseCurrencySymbol: string;
  baseCurrencyDecimalPrecision: number;
}
