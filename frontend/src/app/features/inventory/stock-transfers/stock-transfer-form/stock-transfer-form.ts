import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormArray, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';
import { applyProblem, problemMessage, toProblem } from '../../../../core/http/api-errors';
import { LocationSelect } from '../../../../shared/locations/location-select';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { loadErrorText } from '../../../../shared/scope/problem-text';
import { IdempotentSubmission } from '../stock-transfer-rules';
import { StockTransfersService } from '../stock-transfers.service';
import { TransferLinesEditor, newTransferLine, type TransferLineGroup } from '../transfer-lines-editor/transfer-lines-editor';
import type { CreateStockTransferRequest, StockTransfer, StockTransferLineRequest } from '../stock-transfers.models';

/** Create / edit (Draft) a stock transfer. `?sourceLocationId` / `?destinationLocationId` prefill the endpoints. */
@Component({
  selector: 'app-stock-transfer-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, LocationSelect, TransferLinesEditor],
  templateUrl: './stock-transfer-form.html',
  styleUrl: './stock-transfer-form.scss',
})
export class StockTransferForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(StockTransfersService);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly submission = new IdempotentSubmission();

  readonly form = this.fb.nonNullable.group({
    sourceLocationId: ['', Validators.required],
    destinationLocationId: ['', Validators.required],
    notes: ['', Validators.maxLength(500)],
    lines: this.fb.array<TransferLineGroup>([newTransferLine(this.fb)], Validators.required) as FormArray<TransferLineGroup>,
    submitNow: [false],
  });

  readonly editingId = signal<string | null>(null);
  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  readonly canSubmitNow = computed(() => this.auth.hasPermission('StockTransfers.Create'));
  readonly canPickLocations = computed(() => this.auth.hasPermission('StockLocations.View'));
  readonly prefill = signal<{ source: string | null; destination: string | null }>({ source: null, destination: null });
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    const q = this.route.snapshot.queryParamMap;
    this.prefill.set({ source: q.get('sourceLocationId'), destination: q.get('destinationLocationId') });
    if (this.prefill().source) this.form.controls.sourceLocationId.setValue(this.prefill().source!);
    if (this.prefill().destination) this.form.controls.destinationLocationId.setValue(this.prefill().destination!);

    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
    this.editingId.set(id);
    this.loading.set(true);
    this.service.get(id).subscribe({
      next: (t) => this.populate(t),
      error: (err) => {
        this.loading.set(false);
        this.loadError.set(loadErrorText(err, 'Could not load this transfer.') ?? 'Transfer not found.');
      },
    });
  }

  private populate(t: StockTransfer): void {
    this.loading.set(false);
    if (t.status !== 'Draft') {
      this.loadError.set('Only a draft transfer can be edited.');
      return;
    }
    this.form.controls.lines.clear({ emitEvent: false });
    t.lines.forEach((l) => this.form.controls.lines.push(newTransferLine(this.fb, { productId: l.productId, productUnitId: l.productUnitId, quantity: l.requestedQty }), { emitEvent: false }));
    this.form.patchValue({ sourceLocationId: t.from.id, destinationLocationId: t.to.id, notes: t.notes ?? '' });
    // Let the lines editor load units for the restored rows.
    this.form.controls.lines.updateValueAndValidity();
    this.form.markAsPristine();
  }

  onLocation(field: 'sourceLocationId' | 'destinationLocationId', value: string | null): void {
    const control = this.form.controls[field];
    if (control.value === (value ?? '')) return;
    control.setValue(value ?? '');
    control.markAsDirty();
  }

  locationError(field: 'sourceLocationId' | 'destinationLocationId'): string | null {
    const c = this.form.controls[field];
    if (c.errors?.['server']) return c.errors['server'];
    if (c.errors?.['sameLocation']) return 'Source and destination must be different.';
    if (c.touched && c.errors?.['required']) return field === 'sourceLocationId' ? 'Select a source location.' : 'Select a destination location.';
    return null;
  }

  buildLines(): StockTransferLineRequest[] {
    return this.form.controls.lines.controls.map((g) => {
      const v = g.getRawValue();
      return { productId: v.productId, productUnitId: v.productUnitId, quantity: v.quantity! };
    });
  }

  submit(): void {
    this.serverError.set(null);
    const v = this.form.getRawValue();
    if (v.sourceLocationId && v.sourceLocationId === v.destinationLocationId) {
      this.form.controls.destinationLocationId.setErrors({ sameLocation: true });
      this.form.controls.destinationLocationId.markAsTouched();
      return;
    }
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const base = {
      sourceLocationId: v.sourceLocationId,
      destinationLocationId: v.destinationLocationId,
      notes: v.notes.trim() ? v.notes.trim() : null,
      lines: this.buildLines(),
    };
    const id = this.editingId();
    this.saving.set(true);
    if (id) {
      this.service.update(id, base).subscribe({
        next: (t) => (v.submitNow ? this.submitSaved(t) : this.finish(t, 'Draft saved.')),
        error: (err) => this.fail(err),
      });
      return;
    }
    const request: CreateStockTransferRequest = { ...base, submit: v.submitNow };
    this.service.create(request, this.submission.key).subscribe({
      next: (t) => {
        this.submission.succeeded();
        this.finish(t, v.submitNow ? `${t.transferNo} submitted.` : `${t.transferNo} saved as draft.`);
      },
      error: (err) => this.fail(err),
    });
  }

  /** Edit + "submit now": the draft is saved first, then submitted. A submit failure still leaves the saved draft. */
  private submitSaved(saved: StockTransfer): void {
    this.service.submit(saved.id).subscribe({
      next: (t) => this.finish(t, `${t.transferNo} submitted.`),
      error: (err) => {
        this.saved = true;
        this.saving.set(false);
        this.toast.show(`Draft saved, but it could not be submitted: ${problemMessage(toProblem(err))}`);
        this.router.navigate(['/inventory/stock-transfers', saved.id]);
      },
    });
  }

  private finish(t: StockTransfer, message: string): void {
    this.saving.set(false);
    this.saved = true;
    this.toast.show(message);
    this.router.navigate(['/inventory/stock-transfers', t.id]);
  }

  private fail(err: unknown): void {
    this.saving.set(false);
    this.submission.failed(err);
    this.serverError.set(applyProblem(this.form, toProblem(err)));
  }
}
