import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { AuthService } from '../../../../core/auth/auth.service';
import { applyProblem, toProblem } from '../../../../core/http/api-errors';
import { problemText } from '../../../../shared/scope/problem-text';
import { UiButton } from '../../../../shared/ui/button/button';
import { eligibleVehicles, noVehiclesHint, vehicleLabel } from '../shipment-rules';
import type { AddShipmentVehicleRequest, Shipment, ShipmentEligibleVehicle } from '../shipments.models';
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
  private readonly auth = inject(AuthService);

  readonly shipment = input.required<Shipment>();
  /** Draft / Loading with Shipments.Plan. */
  readonly editable = input(false);
  readonly updated = output<Shipment>();

  readonly master = signal<ShipmentEligibleVehicle[]>([]);
  readonly masterError = signal<string | null>(null);
  /** Null until the list has loaded, so the hint never flashes before the answer. */
  readonly masterLoaded = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly label = vehicleLabel;

  readonly external = computed(() => this.shipment().transportVia === 'ExternalProvider');
  readonly eligible = computed(() =>
    eligibleVehicles(this.master(), this.shipment().branch.id, this.shipment().vehicles.map((v) => v.vehicleId)),
  );

  readonly emptyHint = computed(() =>
    this.masterLoaded() ? noVehiclesHint(this.shipment(), this.eligible().length, this.master().length) : null,
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
      // Only own-fleet shipments pick from the master. The shipment-scoped list needs Shipments.Plan only, so branch planners see the
      // central vehicles and their branch's vehicles; it reloads whenever the shipment changes (a removed vehicle becomes eligible again).
      const shipment = this.shipment();
      if (shipment.transportVia !== 'OwnFleet' || !this.editable()) return;
      this.masterError.set(null);
      this.service.eligibleVehicles(shipment.id).subscribe({
        next: (all) => {
          this.master.set(all);
          this.masterLoaded.set(true);
        },
        error: (err) => this.masterError.set(problemText(err, 'Could not load the vehicles.')),
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
