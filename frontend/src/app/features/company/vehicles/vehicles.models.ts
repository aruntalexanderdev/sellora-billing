/** Mirrors Domain/Inventory/VehicleType.cs (string enum). */
export type VehicleType = 'Van' | 'Truck' | 'Car' | 'Motorcycle' | 'ThreeWheeler' | 'Other';
export const VEHICLE_TYPES: readonly VehicleType[] = ['Van', 'Truck', 'Car', 'Motorcycle', 'ThreeWheeler', 'Other'];

/** Mirrors Api/Contracts/VehicleContracts.cs VehicleDto. */
export interface Vehicle {
  id: string;
  stockLocationId: string;
  vehicleNumber: string;
  vehicleName: string;
  vehicleType: VehicleType;
  assignedBranchId: string | null;
  assignedBranchCode: string | null;
  assignedBranchName: string | null;
  isActive: boolean;
  createdAt: string;
}

/** Mirrors UpdateVehicleRequest (the vehicle number cannot be edited). A null assignedBranchId = central. */
export interface UpdateVehicleRequest {
  vehicleName: string;
  vehicleType: VehicleType;
  assignedBranchId: string | null;
}

/** Mirrors CreateVehicleRequest. */
export interface CreateVehicleRequest extends UpdateVehicleRequest {
  vehicleNumber: string;
}

export interface VehicleQuery {
  assignedBranchId?: string | null;
  unassigned?: boolean;
  vehicleType?: VehicleType | '';
  includeInactive?: boolean;
}
