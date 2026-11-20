export interface CustomerItem {
  id: string;
  name: string;
  phone: string | null;
  address: string | null;
  isWalkIn: boolean;
  isActive: boolean;
  countryId: string | null;
  countryName: string | null;
  stateId: string | null;
  stateName: string | null;
}

export interface UpsertCustomerRequest {
  name: string;
  phone?: string | null;
  address?: string | null;
  countryId: string | null;
  stateId: string | null;
}
