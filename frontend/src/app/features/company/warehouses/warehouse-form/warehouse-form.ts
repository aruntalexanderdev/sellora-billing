import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { applyProblem, problemMessage, toProblem } from '../../../../core/http/api-errors';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import type { AccessibleBranchOption } from '../../branches/branches.models';
import { BranchesService } from '../../branches/branches.service';
import type { Warehouse } from '../warehouses.models';
import { WarehousesService } from '../warehouses.service';

/** Mirrors WarehouseService: 1-20 letters/digits, optionally separated by single hyphens. */
export const WAREHOUSE_CODE_PATTERN = /^[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*$/;

@Component({
  selector: 'app-warehouse-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './warehouse-form.html',
  styleUrl: '../../company-admin.scss',
})
export class WarehouseForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(WarehousesService);
  private readonly branches = inject(BranchesService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly warehouseId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.warehouseId !== null);

  readonly form = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.maxLength(20), Validators.pattern(WAREHOUSE_CODE_PATTERN)]],
    name: ['', [Validators.required, Validators.maxLength(150)]],
    /** '' = company-level warehouse. */
    branchId: [''],
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
    if (!this.isEdit()) {
      this.branches.accessible().subscribe({ next: (b) => this.branchOptions.set(b), error: () => this.branchOptions.set([]) });
      return;
    }
    // Code, branch and ownership are fixed after creation.
    this.form.controls.code.disable();
    this.form.controls.branchId.disable();
    this.loading.set(true);
    this.service.get(this.warehouseId!).subscribe({
      next: (w) => {
        this.load(w);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.serverError.set(toProblem(err)?.status === 404 ? 'Warehouse not found.' : problemMessage(toProblem(err), 'Could not load this warehouse.'));
      },
    });
  }

  load(w: Warehouse): void {
    if (w.branchId) this.branchOptions.set([{ id: w.branchId, code: w.branchCode ?? '', name: w.branchName ?? '', isDefault: false }]);
    this.form.patchValue({ code: w.code, name: w.name, branchId: w.branchId ?? '' });
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
    const request$ = this.isEdit()
      ? this.service.update(this.warehouseId!, { name: v.name.trim() })
      : this.service.create({ code: v.code.trim(), name: v.name.trim(), branchId: v.branchId || null });
    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Warehouse updated.' : 'Warehouse created.');
        this.router.navigateByUrl('/company/warehouses');
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.serverError.set(applyProblem(this.form, toProblem(err)));
      },
    });
  }
}
