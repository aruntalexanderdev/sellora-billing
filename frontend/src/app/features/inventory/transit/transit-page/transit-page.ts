import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { problemMessage, toProblem } from '../../../../core/http/api-errors';
import { LoadError } from '../../../../shared/scope/load-error';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiCompanyViewToggle } from '../../../../shared/ui/company-view-toggle/company-view-toggle';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { consignmentStatusTone, modeLabel } from '../../shipments/shipment-rules';
import { TRANSIT_VIEWS } from '../../shipments/shipments.models';
import type { TransitItem, TransitView } from '../../shipments/shipments.models';
import { endpointLabel } from '../../stock-transfers/stock-transfer-rules';
import { TransitService } from '../transit.service';

export const TRANSIT_PAGE_SIZE = 20;

export const TRANSIT_VIEW_LABELS: Record<TransitView, string> = {
  current: 'Current', incoming: 'Incoming', outgoing: 'Outgoing', completed: 'Completed', discrepancies: 'Discrepancies',
};

/** Goods in (or through) transit per consignment, with five views. Rows expand to per-line quantities. */
@Component({
  selector: 'app-transit-page',
  standalone: true,
  imports: [RouterLink, UiBadge, UiButton, LoadError, UiCompanyViewToggle],
  templateUrl: './transit-page.html',
  styleUrl: './transit-page.scss',
})
export class TransitPage implements OnInit {
  private readonly service = inject(TransitService);

  readonly views = TRANSIT_VIEWS;
  readonly viewLabels = TRANSIT_VIEW_LABELS;
  readonly view = signal<TransitView>('current');
  readonly search = signal('');
  readonly page = signal(1);
  readonly loading = signal(true);
  readonly errorMessage = signal<string | null>(null);
  readonly items = signal<TransitItem[]>([]);
  readonly total = signal(0);
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly hasMore = computed(() => this.page() * TRANSIT_PAGE_SIZE < this.total());

  readonly place = endpointLabel;
  readonly mode = modeLabel;
  readonly tone = consignmentStatusTone;
  readonly qty = (n: number) => formatQuantity(n, 0); // DTO has no base decimals: widening convention

  ngOnInit(): void {
    this.load();
  }

  setView(v: TransitView): void {
    if (v === this.view()) return;
    this.view.set(v);
    this.items.set([]);
    this.expanded.set(new Set());
    this.reload();
  }

  onSearch(v: string): void {
    this.search.set(v.trim());
    this.reload();
  }

  onCompanyViewChange(): void {
    this.reload();
  }

  next(): void {
    this.page.update((p) => p + 1);
    this.load();
  }

  previous(): void {
    this.page.update((p) => Math.max(1, p - 1));
    this.load();
  }

  toggle(id: string): void {
    this.expanded.update((s) => {
      const n = new Set(s);
      if (!n.delete(id)) n.add(id);
      return n;
    });
  }

  retry(): void {
    this.load();
  }

  private reload(): void {
    this.page.set(1);
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.errorMessage.set(null);
    this.service.list({ view: this.view(), search: this.search() || undefined, page: this.page(), pageSize: TRANSIT_PAGE_SIZE }).subscribe({
      next: (res) => {
        this.items.set(res.items);
        this.total.set(res.total);
        this.loading.set(false);
      },
      error: (err) => {
        this.errorMessage.set(problemMessage(toProblem(err)));
        this.loading.set(false);
      },
    });
  }
}
