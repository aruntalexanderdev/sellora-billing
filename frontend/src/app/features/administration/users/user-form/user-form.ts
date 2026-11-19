import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { ContextService } from '../../../../core/context/context.service';
import { UsersService } from '../users.service';
import { userAdminMode } from '../users.logic';
import { RolesService } from '../../roles/roles.service';
import type { RoleListItem } from '../../roles/roles.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

@Component({
  selector: 'app-user-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './user-form.html',
  styleUrl: './user-form.scss',
})
export class UserForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly usersService = inject(UsersService);
  private readonly rolesService = inject(RolesService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly context = inject(ContextService);

  readonly roles = signal<RoleListItem[]>([]);
  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  readonly userId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.userId !== null && this.userId !== 'new');

  /** FI-D44: a holder of only the Branch.Users.* code administers users of the working branch (server forces scope/branch). */
  readonly branchMode = userAdminMode(
    (c) => this.auth.hasPermission(c),
    this.userId !== null && this.userId !== 'new' ? 'Users.Edit' : 'Users.Create',
    this.userId !== null && this.userId !== 'new' ? 'Branch.Users.Edit' : 'Branch.Users.Create',
  ) === 'branch';
  /** Branch edit: the role is not part of the form (managed on the Access & roles page; the PUT omits roleId). */
  readonly branchEdit = this.branchMode && this.userId !== null && this.userId !== 'new';
  readonly workingBranchName = computed(() => this.context.workingBranch()?.name ?? 'the working branch');

  readonly form = this.fb.nonNullable.group({
    firstName: ['', Validators.required],
    lastName: ['', Validators.required],
    username: ['', Validators.required],
    email: ['', [Validators.required, Validators.email]],
    phoneNumber: [''],
    roleId: ['', Validators.required],
    password: [''],
    confirmPassword: [''],
  });

  ngOnInit(): void {
    if (this.branchEdit) this.form.removeControl('roleId' as never);
    // Branch mode never requests company roles (403 for a branch-only admin): active branch roles of the working branch only.
    const roles$ = this.branchMode
      ? this.rolesService.list({ scope: 'Branch', branchId: this.context.workingBranchId() ?? this.context.workingBranch()?.id ?? null })
      : this.rolesService.list();
    // FI-D45: in branch mode roles holding branch administration codes are company-granted only.
    if (!this.branchEdit) {
      roles$.subscribe({
        next: (roles) => this.roles.set(roles.filter((r) => r.isActive && !(this.branchMode && r.holdsBranchAdministration))),
      });
    }

    if (this.isEdit()) {
      this.form.get('password')?.clearValidators();
      this.usersService.get(this.userId!).subscribe({
        next: (u) =>
          this.form.patchValue({
            firstName: u.firstName,
            lastName: u.lastName,
            username: u.username,
            email: u.email,
            phoneNumber: u.phoneNumber ?? '',
            ...(this.branchEdit ? {} : { roleId: u.roleId ?? '' }),
          }),
      });
    } else {
      this.form.get('password')?.setValidators([Validators.required, Validators.minLength(8)]);
      this.form.get('confirmPassword')?.setValidators([Validators.required]);
    }
  }

  submit(): void {
    this.serverError.set(null);

    if (this.form.value.password !== this.form.value.confirmPassword) {
      this.form.get('confirmPassword')?.setErrors({ mismatch: true });
    }

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const v = this.form.getRawValue();

    const request$ = this.isEdit()
      ? this.usersService.update(this.userId!, {
          firstName: v.firstName,
          lastName: v.lastName,
          username: v.username,
          email: v.email,
          phoneNumber: v.phoneNumber || null,
          ...(this.branchEdit ? {} : { roleId: v.roleId }),
        })
      : this.usersService.create({
          firstName: v.firstName,
          lastName: v.lastName,
          username: v.username,
          email: v.email,
          phoneNumber: v.phoneNumber || null,
          password: v.password,
          confirmPassword: v.confirmPassword,
          roleId: v.roleId,
        });

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'User updated.' : 'User created.');
        this.router.navigateByUrl('/administration/users');
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.applyServerErrors(err);
      },
    });
  }

  private applyServerErrors(err: HttpErrorResponse): void {
    const problem = err.error as ApiProblem | undefined;
    if (problem?.errors) {
      for (const [field, messages] of Object.entries(problem.errors)) {
        const control = this.form.get(field.charAt(0).toLowerCase() + field.slice(1));
        control?.setErrors({ server: messages[0] });
      }
      return;
    }
    this.serverError.set(problem?.title ?? 'Something went wrong. Please try again.');
  }
}
