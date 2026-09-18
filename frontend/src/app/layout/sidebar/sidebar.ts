import { Component, computed, inject, input, output } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter, map } from 'rxjs';
import type { SidebarNavGroup } from './sidebar.types';

// Sibling routes often share a URL prefix (e.g. "/sales" and "/sales/new" both have their own
// sidebar item), so `routerLinkActive`'s default prefix matching lights up more than one item at
// once. Instead, pick the single longest matching routerLink across every item in the sidebar —
// this also keeps a parent item (e.g. "Sales") highlighted on pages that have no sidebar entry of
// their own, like a sale's detail view.
function isPrefixMatch(url: string, link: string): boolean {
  return url === link || url.startsWith(link.endsWith('/') ? link : `${link}/`);
}

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './sidebar.html',
  styleUrl: './sidebar.scss',
  host: {
    '[class.sb-open]': 'open()',
  },
})
export class Sidebar {
  private readonly router = inject(Router);

  readonly navGroups = input.required<SidebarNavGroup[]>();
  readonly open = input(false);
  readonly closeRequested = output<void>();

  private readonly currentUrl = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects.split(/[?#]/)[0]),
    ),
    { initialValue: this.router.url.split(/[?#]/)[0] },
  );

  readonly activeLink = computed(() => {
    const url = this.currentUrl();
    let best: string | null = null;
    for (const group of this.navGroups()) {
      for (const item of group.items) {
        if (isPrefixMatch(url, item.routerLink) && (best === null || item.routerLink.length > best.length)) {
          best = item.routerLink;
        }
      }
    }
    return best;
  });
}
