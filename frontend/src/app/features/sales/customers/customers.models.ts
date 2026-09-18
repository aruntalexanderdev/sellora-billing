export interface CustomerItem {
  id: string;
  name: string;
  phone: string | null;
  address: string | null;
  isWalkIn: boolean;
  isActive: boolean;
}

export interface UpsertCustomerRequest {
  name: string;
  phone?: string | null;
  address?: string | null;
}
