import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { PlatformAuthService } from '../../core/platform/platform-auth.service';
import { UiToastHost } from '../../shared/ui/toast/toast-host';

/** D7: the platform console's own layout (outside the company shell; no company/branch selector, no link back into the company app). */
@Component({
  selector: 'app-platform-layout',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, UiToastHost],
  template: `
    <header class="bar">
      <strong>Sellora Platform Console</strong>
      <nav>
        <a routerLink="/platform/companies" routerLinkActive="on">Companies</a>
        <a routerLink="/platform/units" routerLinkActive="on">Units</a>
        <a routerLink="/platform/currencies" routerLinkActive="on">Currencies</a>
        <a routerLink="/platform/countries" routerLinkActive="on">Countries</a>
        <a routerLink="/platform/states" routerLinkActive="on">States</a>
        <a routerLink="/platform/db-releases" routerLinkActive="on">Database releases</a>
      </nav>
      <span class="spacer"></span>
      <span class="who">{{ platform.user()?.name }}</span>
      <button type="button" class="out" (click)="signOut()">Sign out</button>
    </header>
    <main class="page"><router-outlet /></main>
    <ui-toast-host />
  `,
  styles: [`
    :host { display: block; min-height: 100vh; background: var(--color-bg); }
    .bar { display: flex; align-items: center; gap: 22px; padding: 12px 24px; background: var(--color-sidebar-bg); color: #fff; }
    nav { display: flex; gap: 6px; }
    nav a { color: var(--color-sidebar-text); padding: 6px 12px; border-radius: 6px; font-size: 13.5px; font-weight: 600; text-decoration: none; }
    nav a.on, nav a:hover { background: var(--color-sidebar-hover); color: #fff; }
    .spacer { flex: 1; }
    .who { font-size: 13px; color: var(--color-sidebar-text); }
    .out { background: none; border: 1px solid var(--color-sidebar-border); color: #fff; padding: 6px 12px; border-radius: 6px; cursor: pointer; font-weight: 600; }
    .page { max-width: 1100px; margin: 0 auto; padding: 24px; }
  `],
})
export class PlatformLayout {
  readonly platform = inject(PlatformAuthService);
  private readonly router = inject(Router);

  signOut(): void {
    this.platform.clear();
    void this.router.navigate(['/platform/login']);
  }
}
