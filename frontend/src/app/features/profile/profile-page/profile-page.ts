import { Component, computed, inject } from '@angular/core';
import { AuthService } from '../../../core/auth/auth.service';
import { UiCard } from '../../../shared/ui/card/card';
import { UiBadge } from '../../../shared/ui/badge/badge';

@Component({
  selector: 'app-profile-page',
  standalone: true,
  imports: [UiCard, UiBadge],
  templateUrl: './profile-page.html',
  styleUrl: './profile-page.scss',
})
export class ProfilePage {
  private readonly auth = inject(AuthService);

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
}
