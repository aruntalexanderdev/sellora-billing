export interface TaxItem {
  id: string;
  name: string;
  ratePercent: number;
  isActive: boolean;
}

export interface UpsertTaxRequest {
  name: string;
  ratePercent: number;
}
