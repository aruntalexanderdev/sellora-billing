import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { applyProblem, problemMessage, toProblem } from '../../../../core/http/api-errors';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import type { AccessibleBranchOption } from '../../branches/branches.models';
import { BranchesService } from '../../branches/branches.service';
import { VEHICLE_TYPES, type Vehicle, type VehicleType } from '../vehicles.models';
import { VehiclesService } from '../vehicles.service';

/** Server field names that differ from the control names. */
const FIELD_MAP = { VehicleNumber: 'vehicleNumber', VehicleName: 'vehicleName', VehicleType: 'vehicleType', AssignedBranchId: 'assignedBranchId' };

@Component({
  selector: 'app-vehicle-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './vehicle-form.html',
  styleUrl: '../../company-admin.scss',
})
export class VehicleForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(VehiclesService);
  private readonly branches = inject(BranchesService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly vehicleId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.vehicleId !== null);
  readonly vehicleTypes = VEHICLE_TYPES;

  readonly form = this.fb.nonNullable.group({
    vehicleNumber: ['', [Validators.required, Validators.maxLength(40)]],
    vehicleName: ['', [Validators.required, Validators.maxLength(150)]],
    vehicleType: ['Van' as VehicleType, Validators.required],
    /** '' = central (no assigned branch). */
    assignedBranchId: [''],
  });

  readonly branchOptions = signal<AccessibleBranchOption[]>([]);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    this.branches.accessible().subscribe({ next: (b) => this.branchOptions.set(b), error: () => this.branchOptions.set([]) });
    if (!this.isEdit()) return;
    this.form.controls.vehicleNumber.disable();
    this.loading.set(true);
    this.service.get(this.vehicleId!).subscribe({
      next: (v) => {
        this.load(v);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.serverError.set(toProblem(err)?.status === 404 ? 'Vehicle not found.' : problemMessage(toProblem(err), 'Could not load this vehicle.'));
      },
    });
  }

  load(v: Vehicle): void {
    this.form.patchValue({
      vehicleNumber: v.vehicleNumber, vehicleName: v.vehicleName, vehicleType: v.vehicleType, assignedBranchId: v.assignedBranchId ?? '',
    });
    this.form.markAsPristine();
  }

  submit(): void {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving.set(true);
    const v = this.form.getRawValue();
    const body = { vehicleName: v.vehicleName.trim(), vehicleType: v.vehicleType, assignedBranchId: v.assignedBranchId || null };
    const request$ = this.isEdit()
      ? this.service.update(this.vehicleId!, body)
      : this.service.create({ vehicleNumber: v.vehicleNumber.trim(), ...body });
    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Vehicle updated.' : 'Vehicle created.');
        this.router.navigateByUrl('/company/vehicles');
      },
      error: (err: unknown) => {
        this.saving.set(false);
        // An InUse refusal on reassignment arrives as an AssignedBranchId field error and renders under that control.
        this.serverError.set(applyProblem(this.form, toProblem(err), FIELD_MAP));
      },
    });
  }
}
