import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { problemMessage, toProblem } from '../../core/http/api-errors';
import { PlatformAuthService } from '../../core/platform/platform-auth.service';
import { UiButton } from '../../shared/ui/button/button';

/** Platform operator sign-in (D7): a separate credential check from the company login; nothing links here from the company app. */
@Component({
  selector: 'app-platform-login',
  standalone: true,
  imports: [ReactiveFormsModule, UiButton],
  template: `
    <div class="wrap">
      <form class="card" [formGroup]="form" (ngSubmit)="submit()">
        <h1>Platform console</h1>
        <p class="muted">Sign in with a platform administrator account.</p>
        @if (error()) {
          <div class="server-error" role="alert">{{ error() }}</div>
        }
        <label class="field-label" for="pl-user">Username or email</label>
        <input id="pl-user" class="field-input" formControlName="usernameOrEmail" autocomplete="username" />
        @if (form.controls.usernameOrEmail.touched && form.controls.usernameOrEmail.invalid) {
          <div class="field-error">Enter your username or email.</div>
        }
        <label class="field-label" for="pl-pass" style="margin-top: 12px">Password</label>
        <input id="pl-pass" type="password" class="field-input" formControlName="password" autocomplete="current-password" />
        @if (form.controls.password.touched && form.controls.password.invalid) {
          <div class="field-error">Enter your password.</div>
        }
        <div class="actions"><ui-button type="submit" variant="primary" [disabled]="loading()">{{ loading() ? 'Signing in…' : 'Sign in' }}</ui-button></div>
      </form>
    </div>
  `,
  styleUrl: './platform.scss',
  styles: [`.wrap { min-height: 100vh; display: grid; place-items: center; background: var(--color-bg); } .card { width: 360px; margin: 0; }`],
})
export class PlatformLogin {
  private readonly fb = inject(FormBuilder);
  private readonly platform = inject(PlatformAuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly form = this.fb.nonNullable.group({ usernameOrEmail: ['', Validators.required], password: ['', Validators.required] });
  readonly loading = signal(false);
  readonly error = signal<string | null>(
    this.route.snapshot.queryParamMap.get('reason') === 'expired' ? 'Your platform session has ended. Please sign in again.' : null,
  );

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.loading.set(true);
    this.error.set(null);
    this.platform.login(this.form.getRawValue()).subscribe({
      next: () => {
        this.loading.set(false);
        void this.router.navigate(['/platform/companies']);
      },
      error: (err: HttpErrorResponse) => {
        this.loading.set(false);
        // invalid_credentials / account_disabled / platform_admin_required
        this.error.set(problemMessage(toProblem(err)));
      },
    });
  }
}
