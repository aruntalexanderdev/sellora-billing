import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthService } from '../../../core/auth/auth.service';
import { UsersService } from '../../administration/users/users.service';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { UiCard } from '../../../shared/ui/card/card';
import { UiBadge } from '../../../shared/ui/badge/badge';
import { UiButton } from '../../../shared/ui/button/button';
import type { ApiProblem } from '../../../core/auth/auth.models';

@Component({
  selector: 'app-profile-page',
  standalone: true,
  imports: [ReactiveFormsModule, UiCard, UiBadge, UiButton],
  templateUrl: './profile-page.html',
  styleUrl: './profile-page.scss',
})
export class ProfilePage {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly usersService = inject(UsersService);
  private readonly toast = inject(ToastService);

  readonly user = this.auth.currentUser;
  readonly initials = computed(() => {
    const u = this.user();
    return u ? `${u.firstName[0] ?? ''}${u.lastName[0] ?? ''}` : '';
  });

  readonly permissionsByModule = computed(() => {
    const u = this.user();
    if (!u) return [];
    const groups = new Map<string, string[]>();
    for (const code of u.permissions) {
      const module = code.split('.')[0];
      groups.set(module, [...(groups.get(module) ?? []), code]);
    }
    return Array.from(groups.entries())
      .map(([module, codes]) => ({ module, codes: codes.sort() }))
      .sort((a, b) => a.module.localeCompare(b.module));
  });

  // Username editing
  readonly editingUsername = signal(false);
  readonly savingUsername = signal(false);
  readonly usernameForm = this.fb.nonNullable.group({
    username: ['', [Validators.required, Validators.maxLength(60)]],
  });

  startEditUsername(): void {
    this.usernameForm.setValue({ username: this.user()?.username ?? '' });
    this.usernameForm.markAsPristine();
    this.editingUsername.set(true);
  }

  cancelEditUsername(): void {
    this.editingUsername.set(false);
  }

  saveUsername(): void {
    if (this.usernameForm.invalid) {
      this.usernameForm.markAllAsTouched();
      return;
    }
    const username = this.usernameForm.getRawValue().username.trim();
    this.savingUsername.set(true);
    this.usersService.updateOwnUsername(username).subscribe({
      next: () => {
        this.savingUsername.set(false);
        this.editingUsername.set(false);
        this.auth.updateCurrentUser({ username });
        this.toast.show('Username updated.');
      },
      error: (err: HttpErrorResponse) => {
        this.savingUsername.set(false);
        const problem = err.error as ApiProblem | undefined;
        const message = problem?.errors?.['Username']?.[0] ?? problem?.title ?? 'Could not update username.';
        this.usernameForm.controls.username.setErrors({ server: message });
      },
    });
  }

  // Password change
  readonly changingPassword = signal(false);
  readonly savingPassword = signal(false);
  readonly passwordServerError = signal<string | null>(null);
  readonly passwordForm = this.fb.nonNullable.group({
    currentPassword: ['', Validators.required],
    newPassword: ['', [Validators.required, Validators.minLength(8)]],
    confirmPassword: ['', Validators.required],
  });

  startChangePassword(): void {
    this.passwordForm.reset({ currentPassword: '', newPassword: '', confirmPassword: '' });
    this.passwordServerError.set(null);
    this.changingPassword.set(true);
  }

  cancelChangePassword(): void {
    this.changingPassword.set(false);
  }

  savePassword(): void {
    this.passwordServerError.set(null);
    if (this.passwordForm.invalid) {
      this.passwordForm.markAllAsTouched();
      return;
    }
    const v = this.passwordForm.getRawValue();
    if (v.newPassword !== v.confirmPassword) {
      this.passwordForm.controls.confirmPassword.setErrors({ mismatch: true });
      return;
    }

    this.savingPassword.set(true);
    this.usersService.changeOwnPassword(v).subscribe({
      next: () => {
        this.savingPassword.set(false);
        this.changingPassword.set(false);
        this.toast.show('Password updated.');
      },
      error: (err: HttpErrorResponse) => {
        this.savingPassword.set(false);
        const problem = err.error as ApiProblem | undefined;
        if (problem?.errors?.['CurrentPassword']) {
          this.passwordForm.controls.currentPassword.setErrors({ server: problem.errors['CurrentPassword'][0] });
          return;
        }
        if (problem?.errors?.['NewPassword']) {
          this.passwordForm.controls.newPassword.setErrors({ server: problem.errors['NewPassword'][0] });
          return;
        }
        this.passwordServerError.set(problem?.title ?? 'Could not update password.');
      },
    });
  }
}
