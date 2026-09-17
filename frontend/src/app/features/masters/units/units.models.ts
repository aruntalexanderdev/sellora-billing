export type UnitType = 'Weight' | 'Volume' | 'Count' | 'Other';

export interface UnitItem {
  id: string;
  name: string;
  shortCode: string;
  type: UnitType;
  isActive: boolean;
}

export interface UpsertUnitRequest {
  name: string;
  shortCode: string;
  type: UnitType;
}
