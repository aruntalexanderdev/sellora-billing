import { Injectable, inject, signal } from '@angular/core';
import { ActivatedRouteSnapshot, NavigationEnd, Router } from '@angular/router';

export interface Breadcrumb {
  group: string;
  page: string;
}

function deepestSnapshot(snapshot: ActivatedRouteSnapshot): ActivatedRouteSnapshot {
  return snapshot.firstChild ? deepestSnapshot(snapshot.firstChild) : snapshot;
}

@Injectable({ providedIn: 'root' })
export class BreadcrumbService {
  private readonly router = inject(Router);
  readonly current = signal<Breadcrumb>({ group: '', page: '' });

  constructor() {
    this.router.events.subscribe((event) => {
      if (!(event instanceof NavigationEnd)) return;
      const leaf = deepestSnapshot(this.router.routerState.snapshot.root);
      const data = leaf.data as Partial<Breadcrumb>;
      this.current.set({ group: data.group ?? '', page: data.page ?? '' });
    });
  }
}
