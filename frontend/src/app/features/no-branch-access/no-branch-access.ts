import { Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { UiButton } from '../../shared/ui/button/button';

/** Shown for 403 `branch_required` / `no_branch_access`: the user has no branch allocation in this company. */
@Component({
  selector: 'app-no-branch-access',
  standalone: true,
  imports: [UiButton],
  templateUrl: './no-branch-access.html',
  styleUrl: './no-branch-access.scss',
})
export class NoBranchAccess {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly user = this.auth.currentUser;

  signOut(): void {
    this.auth.logout();
    void this.router.navigateByUrl('/login');
  }
}
