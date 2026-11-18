import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ReactiveFormsModule, Validators } from '@angular/forms';
import { FormBuilder } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import { applyProblem, toProblem } from '../../../../core/http/api-errors';
import { IdempotentSubmission } from '../../../../core/idempotency';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';
import { LocationSelect } from '../../../../shared/locations/location-select';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { REFERENCE_MAX, REFERENCE_LABELS, TRANSPORT_MODE_LABELS, TRANSPORT_VIA_LABELS, referenceLabel, referenceRequired, validateHeader } from '../shipment-rules';
import { TRANSPORT_MODES } from '../shipments.models';
import type { CreateShipmentRequest, Shipment, TransportMode, TransportVia, UpdateShipmentRequest } from '../shipments.models';
import { ShipmentsService } from '../shipments.service';

/**
 * Shipment header. Create mode (routed, no `shipment` input) picks the source and posts with an Idempotency-Key; edit mode
 * (`shipment` input, used by the planner while Draft/Loading) replaces the header and never changes the source.
 */
@Component({
  selector: 'app-shipment-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, LocationSelect],
  templateUrl: './shipment-form.html',
  styleUrl: './shipment-form.scss',
})
export class ShipmentForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(ShipmentsService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly submission = new IdempotentSubmission();

  /** Present = edit the header of this shipment. */
  readonly shipment = input<Shipment | null>(null);
  readonly saved = output<Shipment>();
  readonly cancelled = output<void>();

  readonly modes = TRANSPORT_MODES;
  readonly modeLabels = TRANSPORT_MODE_LABELS;
  readonly viaLabels = TRANSPORT_VIA_LABELS;
  readonly vias: TransportVia[] = ['OwnFleet', 'ExternalProvider'];
  readonly referenceMax = REFERENCE_MAX;

  readonly form = this.fb.nonNullable.group({
    sourceLocationId: ['', Validators.required],
    transportMode: ['Road' as TransportMode, Validators.required],
    transportVia: ['OwnFleet' as TransportVia, Validators.required],
    providerName: ['', Validators.maxLength(120)],
    providerContact: ['', Validators.maxLength(120)],
    transportReference: ['', Validators.maxLength(REFERENCE_MAX)],
    plannedDispatchDate: [''],
    notes: ['', Validators.maxLength(500)],
  });

  private readonly value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private done = false;

  readonly mode = computed(() => (this.value().transportMode ?? 'Road') as TransportMode);
  readonly via = computed(() => (this.value().transportVia ?? 'OwnFleet') as TransportVia);
  readonly refLabel = computed(() => referenceLabel(this.mode()));
  readonly refRequired = computed(() => referenceRequired(this.mode()));
  readonly refHint = computed(() =>
    this.mode() === 'Road' ? 'Optional for road: the vehicles carry the reference.' : `Required for ${REFERENCE_LABELS[this.mode()].toLowerCase()} shipments.`,
  );
  readonly external = computed(() => this.via() === 'ExternalProvider');
  readonly errors = computed(() =>
    this.submitted()
      ? validateHeader({ mode: this.mode(), via: this.via(), providerName: this.value().providerName ?? '', reference: this.value().transportReference ?? '' })
      : {},
  );
  readonly canPickLocations = computed(() => this.auth.hasPermission('StockLocations.View'));
  readonly editing = computed(() => this.shipment() !== null);

  hasUnsavedChanges(): boolean {
    return !this.done && !this.editing() && this.form.dirty;
  }

  ngOnInit(): void {
    const s = this.shipment();
    if (!s) return;
    this.form.controls.sourceLocationId.setValue(s.source.id);
    this.form.patchValue({
      transportMode: s.transportMode,
      transportVia: s.transportVia,
      providerName: s.providerName ?? '',
      providerContact: s.providerContact ?? '',
      transportReference: s.transportReference ?? '',
      plannedDispatchDate: s.plannedDispatchDate ?? '',
      notes: s.notes ?? '',
    });
    this.form.markAsPristine();
  }

  onSource(value: string | null): void {
    const c = this.form.controls.sourceLocationId;
    if (c.value === (value ?? '')) return;
    c.setValue(value ?? '');
    c.markAsDirty();
  }

  serverOf(field: keyof ReturnType<typeof this.form.getRawValue>): string | null {
    return this.form.controls[field].errors?.['server'] ?? null;
  }

  submit(): void {
    this.serverError.set(null);
    this.submitted.set(true);
    if (this.form.invalid || Object.keys(this.errors()).length > 0) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const header: UpdateShipmentRequest = {
      transportMode: v.transportMode,
      transportVia: v.transportVia,
      providerName: v.transportVia === 'ExternalProvider' && v.providerName.trim() ? v.providerName.trim() : null,
      providerContact: v.transportVia === 'ExternalProvider' && v.providerContact.trim() ? v.providerContact.trim() : null,
      transportReference: v.transportReference.trim() || null,
      plannedDispatchDate: v.plannedDispatchDate || null,
      notes: v.notes.trim() || null,
    };
    this.saving.set(true);
    const current = this.shipment();
    if (current) {
      this.service.update(current.id, header).subscribe({
        next: (s) => {
          this.saving.set(false);
          this.done = true;
          this.toast.show('Shipment updated.');
          this.saved.emit(s);
        },
        error: (err) => this.fail(err),
      });
      return;
    }
    const request: CreateShipmentRequest = { sourceLocationId: v.sourceLocationId, ...header };
    this.service.create(request, this.submission.keyFor(request)).subscribe({
      next: (s) => {
        this.saving.set(false);
        this.submission.succeeded();
        this.done = true;
        this.toast.show(`${s.shipmentNo} created.`);
        this.saved.emit(s);
        this.router.navigate(['/inventory/shipments', s.id]);
      },
      error: (err) => this.fail(err),
    });
  }

  private fail(err: unknown): void {
    this.saving.set(false);
    this.submission.failed(err);
    this.serverError.set(applyProblem(this.form, toProblem(err)));
  }
}
