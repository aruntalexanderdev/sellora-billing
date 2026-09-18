import { Component, inject } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { UiButton } from '../../shared/ui/button/button';

@Component({
  selector: 'app-permission-denied',
  standalone: true,
  imports: [UiButton],
  templateUrl: './permission-denied.html',
  styleUrl: './permission-denied.scss',
})
export class PermissionDenied {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly auth = inject(AuthService);

  readonly permission = this.route.snapshot.queryParamMap.get('permission');

  goToDashboard(): void {
    this.router.navigateByUrl('/dashboard');
  }
}
