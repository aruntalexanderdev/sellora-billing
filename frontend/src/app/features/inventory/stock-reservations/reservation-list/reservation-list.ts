import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import { problemMessage, toProblem } from '../../../../core/http/api-errors';
import { LocationSelect } from '../../../../shared/locations/location-select';
import { LoadError } from '../../../../shared/scope/load-error';
import { problemText } from '../../../../shared/scope/problem-text';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiCompanyViewToggle } from '../../../../shared/ui/company-view-toggle/company-view-toggle';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { ReasonPrompt } from '../../../administration/access-ui/reason-prompt';
import { endpointLabel } from '../../stock-transfers/stock-transfer-rules';
import { reservationStatusTone } from '../../shipments/shipment-rules';
import { RESERVATION_STATUSES } from '../../shipments/shipments.models';
import type { StockReservation, StockReservationStatus } from '../../shipments/shipments.models';
import { ReservationsService } from '../reservations.service';

export const RESERVATION_PAGE_SIZE = 20;

/** Stock reservations, oldest first (server order). Release (with a reason) needs Reservations.Release and an Active reservation. */
@Component({
  selector: 'app-reservation-list',
  standalone: true,
  imports: [RouterLink, DatePipe, UiBadge, UiButton, LoadError, LocationSelect, UiCompanyViewToggle, ReasonPrompt],
  templateUrl: './reservation-list.html',
  styleUrl: './reservation-list.scss',
})
export class ReservationList implements OnInit {
  private readonly service = inject(ReservationsService);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);

  readonly statuses = RESERVATION_STATUSES;
  readonly status = signal<StockReservationStatus>('Active');
  readonly locationId = signal<string | null>(null);
  readonly page = signal(1);
  readonly loading = signal(true);
  readonly errorMessage = signal<string | null>(null);
  readonly items = signal<StockReservation[]>([]);
  readonly total = signal(0);
  readonly releasing = signal<StockReservation | null>(null);
  readonly busy = signal(false);
  readonly releaseError = signal<string | null>(null);

  readonly canRelease = computed(() => this.auth.hasPermission('Reservations.Release'));
  readonly hasMore = computed(() => this.page() * RESERVATION_PAGE_SIZE < this.total());
  readonly place = endpointLabel;
  readonly tone = reservationStatusTone;
  readonly qty = (r: StockReservation) => formatQuantity(r.baseQty, 3);

  /** Release is offered only on Active reservations, and only to Reservations.Release holders. */
  readonly canReleaseRow = (r: StockReservation) => this.canRelease() && r.status === 'Active';

  ngOnInit(): void {
    this.load();
  }

  onStatus(v: string): void {
    this.status.set((RESERVATION_STATUSES.find((s) => s === v) ?? 'Active'));
    this.reload();
  }

  onLocation(v: string | null): void {
    this.locationId.set(v);
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

  startRelease(r: StockReservation): void {
    if (!this.canReleaseRow(r)) return;
    this.releaseError.set(null);
    this.releasing.set(r);
  }

  cancelRelease(): void {
    this.releasing.set(null);
    this.releaseError.set(null);
  }

  confirmRelease(reason: string | null): void {
    const r = this.releasing();
    if (!r || !reason || this.busy()) return;
    this.busy.set(true);
    this.service.release(r.id, reason).subscribe({
      next: () => {
        this.busy.set(false);
        this.releasing.set(null);
        this.toast.show('Reservation released; the allocation was cancelled.');
        this.load();
      },
      error: (err) => {
        this.busy.set(false);
        this.releaseError.set(problemText(err, 'Could not release the reservation.'));
      },
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
    this.service.list({ status: this.status(), locationId: this.locationId(), page: this.page(), pageSize: RESERVATION_PAGE_SIZE }).subscribe({
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
