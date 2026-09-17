import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { UnitsService } from '../units.service';
import type { UnitType } from '../units.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

@Component({
  selector: 'app-unit-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './unit-form.html',
  styleUrl: '../../../administration/users/user-form/user-form.scss',
})
export class UnitForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly unitsService = inject(UnitsService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly unitTypes: UnitType[] = ['Weight', 'Volume', 'Count', 'Other'];
  readonly unitId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.unitId !== null && this.unitId !== 'new');

  readonly form = this.fb.nonNullable.group({
    name: ['', Validators.required],
    shortCode: ['', Validators.required],
    type: ['Count' as UnitType, Validators.required],
  });

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    if (this.isEdit()) {
      this.unitsService.list().subscribe({
        next: (units) => {
          const unit = units.find((u) => u.id === this.unitId);
          if (unit) this.form.patchValue({ name: unit.name, shortCode: unit.shortCode, type: unit.type });
        },
      });
    }
  }

  submit(): void {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const request$ = this.isEdit()
      ? this.unitsService.update(this.unitId!, this.form.getRawValue())
      : this.unitsService.create(this.form.getRawValue());

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Unit updated.' : 'Unit created.');
        this.router.navigateByUrl('/administration/units');
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.serverError.set(problem?.title ?? Object.values(problem?.errors ?? {})[0]?.[0] ?? 'Something went wrong.');
      },
    });
  }
}
