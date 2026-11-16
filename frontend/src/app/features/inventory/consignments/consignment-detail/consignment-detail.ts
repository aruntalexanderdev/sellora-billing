import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { loadErrorText } from '../../../../shared/scope/problem-text';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { endpointLabel } from '../../stock-transfers/stock-transfer-rules';
import { consignmentStatusTone, modeLabel, referenceLabel, shipmentStatusLabel, TRANSPORT_VIA_LABELS } from '../../shipments/shipment-rules';
import type { ConsignmentDetail } from '../../shipments/shipments.models';
import { ConsignmentsService } from '../consignments.service';

/** One consignment as its reader sees it. Incoming readers get no shipment link, other destinations or other vehicles. */
@Component({
  selector: 'app-consignment-detail',
  standalone: true,
  imports: [RouterLink, UiBadge],
  templateUrl: './consignment-detail.html',
  styleUrl: './consignment-detail.scss',
})
export class ConsignmentDetailPage implements OnInit {
  private readonly service = inject(ConsignmentsService);
  private readonly route = inject(ActivatedRoute);

  readonly consignment = signal<ConsignmentDetail | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  /** Outgoing readers may open the planner; incoming never get a shipment id. */
  readonly shipmentLink = computed(() => {
    const c = this.consignment();
    return c && c.direction === 'outgoing' && c.shipmentId ? ['/inventory/shipments', c.shipmentId] : null;
  });

  readonly place = endpointLabel;
  readonly mode = modeLabel;
  readonly refLabel = referenceLabel;
  readonly shipmentStatus = shipmentStatusLabel;
  readonly tone = consignmentStatusTone;
  readonly viaLabels = TRANSPORT_VIA_LABELS;
  readonly qty = (n: number) => formatQuantity(n, 0);

  ngOnInit(): void {
    this.service.get(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (c) => {
        this.consignment.set(c);
        this.loading.set(false);
      },
      error: (err) => {
        this.loadError.set(loadErrorText(err, 'Could not load this consignment.'));
        this.loading.set(false);
      },
    });
  }
}
