import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { AuthService } from '../../../../core/auth/auth.service';
import { applyProblem, toProblem } from '../../../../core/http/api-errors';
import { problemText } from '../../../../shared/scope/problem-text';
import { UiButton } from '../../../../shared/ui/button/button';
import { VehiclesService } from '../../../company/vehicles/vehicles.service';
import type { Vehicle } from '../../../company/vehicles/vehicles.models';
import { eligibleVehicles, vehicleLabel } from '../shipment-rules';
import type { AddShipmentVehicleRequest, Shipment } from '../shipments.models';
import { ShipmentsService } from '../shipments.service';

/** Vehicles (legs) of a shipment: own fleet from the vehicle master, or external number/description; driver optional. */
@Component({
  selector: 'app-shipment-vehicles-panel',
  standalone: true,
  imports: [ReactiveFormsModule, UiButton],
  templateUrl: './shipment-vehicles-panel.html',
  styleUrl: './shipment-vehicles-panel.scss',
})
export class ShipmentVehiclesPanel {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(ShipmentsService);
  private readonly vehicles = inject(VehiclesService);
  private readonly auth = inject(AuthService);

  readonly shipment = input.required<Shipment>();
  /** Draft / Loading with Shipments.Plan. */
  readonly editable = input(false);
  readonly updated = output<Shipment>();

  readonly master = signal<Vehicle[]>([]);
  readonly masterError = signal<string | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly label = vehicleLabel;

  readonly external = computed(() => this.shipment().transportVia === 'ExternalProvider');
  readonly eligible = computed(() =>
    eligibleVehicles(this.master(), this.shipment().branch.id, this.shipment().vehicles.map((v) => v.vehicleId)),
  );

  readonly form = this.fb.nonNullable.group({
    vehicleId: [''],
    externalVehicleNumber: [''],
    externalDescription: [''],
    driverName: [''],
    driverPhone: [''],
  });

  constructor() {
    effect(() => {
      // Only own-fleet shipments pick from the master; the list needs the vehicles view permission.
      if (this.shipment().transportVia !== 'OwnFleet' || !this.editable()) return;
      this.vehicles.list().subscribe({
        next: (all) => this.master.set(all),
        error: (err) => this.masterError.set(problemText(err, 'Could not load the vehicle master.')),
      });
    });
  }

  readonly canRemove = computed(() => this.editable() && this.auth.hasPermission('Shipments.Plan'));

  add(): void {
    const v = this.form.getRawValue();
    const own = !this.external();
    const request: AddShipmentVehicleRequest = {
      vehicleId: own ? v.vehicleId || null : null,
      externalVehicleNumber: own ? null : v.externalVehicleNumber.trim() || null,
      externalDescription: own ? null : v.externalDescription.trim() || null,
      driverName: v.driverName.trim() || null,
      driverPhone: v.driverPhone.trim() || null,
    };
    if (own && !request.vehicleId) {
      this.form.controls.vehicleId.setErrors({ server: 'Select a vehicle of the own fleet.' });
      return;
    }
    if (!own && !request.externalVehicleNumber) {
      this.form.controls.externalVehicleNumber.setErrors({ server: 'Enter the external vehicle number.' });
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.service.addVehicle(this.shipment().id, request).subscribe({
      next: (s) => {
        this.busy.set(false);
        this.form.reset();
        this.updated.emit(s);
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(applyProblem(this.form, toProblem(err)));
      },
    });
  }

  remove(shipmentVehicleId: string): void {
    this.busy.set(true);
    this.error.set(null);
    this.service.removeVehicle(this.shipment().id, shipmentVehicleId).subscribe({
      next: (s) => {
        this.busy.set(false);
        this.updated.emit(s);
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(problemText(err, 'Could not remove the vehicle.'));
      },
    });
  }

  serverOf(field: 'vehicleId' | 'externalVehicleNumber'): string | null {
    return this.form.controls[field].errors?.['server'] ?? null;
  }
}
