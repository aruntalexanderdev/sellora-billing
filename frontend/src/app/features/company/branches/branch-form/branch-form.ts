import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';
import { applyProblem, problemMessage, toProblem } from '../../../../core/http/api-errors';
import { GeoSelect } from '../../../../shared/geo/geo-select';
import type { GeoStoredLink } from '../../../../shared/geo/geo.models';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { blankToNull } from '../../company-admin';
import type { Branch, CreateBranchRequest } from '../branches.models';
import { BranchesService } from '../branches.service';

/** Mirrors BranchService: code 1-6 letters/digits; prefix up to 10 letters/digits/hyphens, not starting or ending with '-'. */
export const BRANCH_CODE_PATTERN = /^[A-Za-z0-9]{1,6}$/;
export const INVOICE_PREFIX_PATTERN = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,8}[A-Za-z0-9])?$/;

@Component({
  selector: 'app-branch-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, GeoSelect],
  templateUrl: './branch-form.html',
  styleUrl: '../../company-admin.scss',
})
export class BranchForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(BranchesService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly branchId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.branchId !== null);

  readonly form = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(BRANCH_CODE_PATTERN)]],
    name: ['', [Validators.required, Validators.maxLength(150)]],
    addressLine: ['', Validators.maxLength(300)],
    city: ['', Validators.maxLength(80)],
    countryId: [''],
    stateId: [''],
    phone: ['', Validators.maxLength(30)],
    email: ['', [Validators.email, Validators.maxLength(200)]],
    gstin: ['', Validators.maxLength(20)],
    invoicePrefix: ['', Validators.pattern(INVOICE_PREFIX_PATTERN)],
  });

  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly isDefault = signal(false);
  /** Stored links (possibly inactive) stay selectable; the legacy text is a hint while no state is linked (D6, D21). */
  readonly storedCountry = signal<GeoStoredLink | null>(null);
  readonly storedState = signal<GeoStoredLink | null>(null);
  readonly legacyState = signal<string | null>(null);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    if (!this.isEdit()) return;
    this.form.controls.code.disable();
    this.loading.set(true);
    this.service.get(this.branchId!).subscribe({
      next: (b) => {
        this.load(b);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.serverError.set(toProblem(err)?.status === 404 ? 'Branch not found.' : problemMessage(toProblem(err), 'Could not load this branch.'));
      },
    });
  }

  load(b: Branch): void {
    this.isDefault.set(b.isDefault);
    this.storedCountry.set(b.countryId ? { id: b.countryId, name: b.countryName ?? '' } : null);
    this.storedState.set(b.stateId ? { id: b.stateId, name: b.stateName ?? '' } : null);
    this.legacyState.set(b.stateId ? null : b.state);
    this.form.patchValue({
      code: b.code, name: b.name, addressLine: b.addressLine ?? '', city: b.city ?? '', countryId: b.countryId ?? '', stateId: b.stateId ?? '', phone: b.phone ?? '',
      email: b.email ?? '', gstin: b.gstin ?? '', invoicePrefix: b.invoicePrefix ?? '',
    });
    // The default branch always uses the company prefix (Settings); the server rejects an own prefix.
    if (b.isDefault) this.form.controls.invoicePrefix.disable();
    this.form.markAsPristine();
  }

  buildRequest(): CreateBranchRequest {
    const v = this.form.getRawValue();
    return {
      code: v.code.trim(),
      name: v.name.trim(),
      addressLine: blankToNull(v.addressLine),
      city: blankToNull(v.city),
      phone: blankToNull(v.phone),
      email: blankToNull(v.email),
      gstin: blankToNull(v.gstin),
      invoicePrefix: this.isDefault() ? null : blankToNull(v.invoicePrefix),
      countryId: blankToNull(v.countryId),
      stateId: v.countryId ? blankToNull(v.stateId) : null,
    };
  }

  submit(): void {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving.set(true);
    const { code, ...update } = this.buildRequest();
    const request$ = this.isEdit() ? this.service.update(this.branchId!, update) : this.service.create({ code, ...update });
    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Branch updated.' : 'Branch created.');
        this.router.navigateByUrl('/company/branches');
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.serverError.set(applyProblem(this.form, toProblem(err)));
      },
    });
  }
}
