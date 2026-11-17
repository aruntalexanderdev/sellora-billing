import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import { ContextService } from '../../../../core/context/context.service';
import { toProblem } from '../../../../core/http/api-errors';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { loadErrorText } from '../../../../shared/scope/problem-text';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { UiButton } from '../../../../shared/ui/button/button';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { endpointLabel } from '../../stock-transfers/stock-transfer-rules';
import {
  DISPOSITION_ACTION_LABELS, DISPOSITION_BUCKETS, DISPOSITION_NOTE, RECEIPT_SHORTFALL_NOTE, bucketBase, bucketDispositionedBase, bucketOpenBase, canReceive, canResolve, hasDispositionable,
  consignmentStatusTone, inTransitBase, modeLabel, plannerProblemMessage, receiptActions, receiptStatusTone, referenceLabel, remainingToAccountBase, shipmentStatusLabel,
  RECEIPT_STATUS_LABELS, TRANSPORT_VIA_LABELS, type ReceiptActionState,
} from '../../shipments/shipment-rules';
import type { ConsignmentDetail, ConsignmentDetailLine, ConsignmentDisposition, ConsignmentReceipt, DispositionBucket } from '../../shipments/shipments.models';
import { ConsignmentsService } from '../consignments.service';
import { DispositionDialog } from '../disposition-dialog/disposition-dialog';
import { ReceiptDialog } from '../receipt-dialog/receipt-dialog';
import { ReceiptRejectDialog } from '../receipt-reject-dialog/receipt-reject-dialog';

/** One consignment as its reader sees it. Incoming readers get no shipment link, other destinations or other vehicles. Receiving: verify (record), then post. */
@Component({
  selector: 'app-consignment-detail',
  standalone: true,
  imports: [RouterLink, DatePipe, UiBadge, UiButton, ReceiptDialog, ReceiptRejectDialog, DispositionDialog],
  templateUrl: './consignment-detail.html',
  styleUrl: './consignment-detail.scss',
})
export class ConsignmentDetailPage implements OnInit {
  private readonly service = inject(ConsignmentsService);
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(AuthService);
  private readonly context = inject(ContextService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly receiptSubmission = new IdempotentSubmission();
  private readonly postSubmissions = new Map<string, IdempotentSubmission>();
  private readonly approveSubmissions = new Map<string, IdempotentSubmission>();
  private readonly rejectSubmissions = new Map<string, IdempotentSubmission>();
  private readonly dispositionSubmission = new IdempotentSubmission();

  readonly consignment = signal<ConsignmentDetail | null>(null);
  readonly receipts = signal<ConsignmentReceipt[]>([]);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly dispositions = signal<ConsignmentDisposition[]>([]);
  readonly receiptOpen = signal(false);
  readonly dispositionOpen = signal(false);
  readonly rejecting = signal<ConsignmentReceipt | null>(null);
  readonly busyReceiptId = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);

  /** Outgoing readers may open the planner; incoming never get a shipment id. */
  readonly shipmentLink = computed(() => {
    const c = this.consignment();
    return c && c.direction === 'outgoing' && c.shipmentId ? ['/inventory/shipments', c.shipmentId] : null;
  });
  /** Record receipt / Post: StockTransfers.Receive while the consignment is InTransit or PartiallyReceived. */
  readonly receivable = computed(() => {
    const c = this.consignment();
    return !!c && canReceive(c.status, (code) => this.auth.hasPermission(code));
  });
  /** Resolve discrepancy: company-level StockTransfers.Resolve while some POSTED quantity is still open to a disposition. */
  readonly resolvable = computed(() =>
    canResolve(hasDispositionable(this.consignment()?.lines ?? [], this.receipts()), (code) => this.context.canCompanyView(code)));
  readonly hasDiscrepancyData = computed(() => (this.consignment()?.lines ?? []).some((l) => DISPOSITION_BUCKETS.some((b) => bucketBase(l, b) > 0)) || this.dispositions().length > 0);
  private readonly currentUserId = computed(() => this.auth.currentUser?.()?.id ?? null);
  readonly hasRemaining = computed(() => (this.consignment()?.lines ?? []).some((l) => remainingToAccountBase(l) > 0));
  readonly dispatched = computed(() => !!this.consignment()?.dispatchedAt);

  readonly place = endpointLabel;
  readonly mode = modeLabel;
  readonly refLabel = referenceLabel;
  readonly shipmentStatus = shipmentStatusLabel;
  readonly tone = consignmentStatusTone;
  readonly viaLabels = TRANSPORT_VIA_LABELS;
  readonly shortfallNote = RECEIPT_SHORTFALL_NOTE;
  readonly receiptStatusLabels = RECEIPT_STATUS_LABELS;
  readonly receiptTone = receiptStatusTone;
  readonly buckets = DISPOSITION_BUCKETS;
  readonly actionLabels = DISPOSITION_ACTION_LABELS;
  readonly disclaimer = DISPOSITION_NOTE;
  readonly bucketTotal = (l: ConsignmentDetailLine, b: DispositionBucket) => bucketBase(l, b);
  readonly bucketDone = (l: ConsignmentDetailLine, b: DispositionBucket) => bucketDispositionedBase(l, b);
  readonly bucketOpen = (l: ConsignmentDetailLine, b: DispositionBucket) => bucketOpenBase(l, b);
  readonly qty = (n: number) => formatQuantity(n, 0);
  /** Base quantities: the DTOs carry no base decimals, so use the screen convention (widens to the stored precision, never hides it). */
  readonly base = (n: number) => formatQuantity(n, 0);
  readonly remainingBase = remainingToAccountBase;
  readonly inTransit = inTransitBase;

  ngOnInit(): void {
    this.service.get(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (c) => {
        this.consignment.set(c);
        this.loading.set(false);
        if (c.receiptCount > 0) this.loadReceipts();
        if (c.dispositionCount > 0) this.loadDispositions();
      },
      error: (err) => {
        this.loadError.set(loadErrorText(err, 'Could not load this consignment.'));
        this.loading.set(false);
      },
    });
  }

  private loadReceipts(): void {
    this.service.receipts(this.consignment()!.id).subscribe({ next: (r) => this.receipts.set(r) });
  }

  private loadDispositions(): void {
    this.service.dispositions(this.consignment()!.id).subscribe({ next: (d) => this.dispositions.set(d) });
  }

  /** Quantities and statuses change after a receipt, a post or a disposition: re-read all. */
  private refresh(): void {
    const id = this.consignment()!.id;
    this.service.get(id).subscribe({
      next: (c) => {
        this.consignment.set(c);
        if (c.dispositionCount > 0) this.loadDispositions();
      },
    });
    this.loadReceipts();
  }

  openReceipt(): void {
    this.actionError.set(null);
    this.receiptOpen.set(true);
  }

  closeReceipt(): void {
    this.receiptOpen.set(false);
    if (this.receiptSubmission.hasAmbiguousFailure) {
      this.receiptSubmission.acknowledged();
      this.refresh();
    }
  }

  onReceiptRecorded(receipt?: ConsignmentReceipt): void {
    this.receiptOpen.set(false);
    this.toast.show(
      receipt?.status === 'PendingApproval'
        ? 'Receipt recorded. A supervisor must approve it before stock moves.'
        : 'Receipt recorded. Post it to move the accepted quantity into stock.',
    );
    this.refresh();
  }

  submissionForReceipt(): IdempotentSubmission {
    return this.receiptSubmission;
  }

  /** Per receipt: which of Post / Approve / Reject to offer (policy snapshot x permission x recorder). */
  actions(r: ConsignmentReceipt) {
    return receiptActions(r, (code) => this.auth.hasPermission(code), this.currentUserId());
  }

  canPost(r: ConsignmentReceipt): boolean {
    return this.actions(r).post.state === 'enabled';
  }

  isEnabled(a: ReceiptActionState): boolean {
    return a.state === 'enabled';
  }

  openResolve(): void {
    this.actionError.set(null);
    this.dispositionOpen.set(true);
  }

  closeResolve(): void {
    this.dispositionOpen.set(false);
    if (this.dispositionSubmission.hasAmbiguousFailure) {
      this.dispositionSubmission.acknowledged();
      this.refresh();
    }
  }

  onDispositionRecorded(): void {
    this.dispositionOpen.set(false);
    this.toast.show('Discrepancy resolved.');
    this.refresh();
  }

  submissionForDisposition(): IdempotentSubmission {
    return this.dispositionSubmission;
  }

  openReject(r: ConsignmentReceipt): void {
    if (this.actions(r).reject.state !== 'enabled') return;
    this.actionError.set(null);
    this.rejecting.set(r);
  }

  closeReject(): void {
    const r = this.rejecting();
    this.rejecting.set(null);
    if (r && this.rejectSubmissions.get(r.id)?.hasAmbiguousFailure) {
      this.rejectSubmissions.get(r.id)!.acknowledged();
      this.refresh();
    }
  }

  onRejected(): void {
    this.rejecting.set(null);
    this.toast.show('Receipt rejected for correction.');
    this.refresh();
  }

  submissionForReject(r: ConsignmentReceipt): IdempotentSubmission {
    let sub = this.rejectSubmissions.get(r.id);
    if (!sub) this.rejectSubmissions.set(r.id, (sub = new IdempotentSubmission()));
    return sub;
  }

  async approve(r: ConsignmentReceipt): Promise<void> {
    if (this.actions(r).approve.state !== 'enabled' || this.busyReceiptId()) return;
    const ok = await this.confirm.ask({
      title: `Approve receipt #${r.sequence}`,
      body: 'Approving posts the receipt: the accepted quantity moves from Transit into the destination stock. Damaged, rejected and missing quantities stay in transit. This cannot be undone here.',
      confirmLabel: 'Approve and post',
    });
    if (!ok) return;
    let sub = this.approveSubmissions.get(r.id);
    if (!sub) this.approveSubmissions.set(r.id, (sub = new IdempotentSubmission()));
    const submission = sub;
    this.busyReceiptId.set(r.id);
    this.actionError.set(null);
    this.service.approveReceipt(this.consignment()!.id, r.id, submission.key).subscribe({
      next: () => {
        this.busyReceiptId.set(null);
        submission.succeeded();
        this.toast.show('Receipt approved and posted.');
        this.refresh();
      },
      error: (err) => {
        this.busyReceiptId.set(null);
        submission.failed(err);
        this.actionError.set(plannerProblemMessage(toProblem(err)));
      },
    });
  }

  async post(r: ConsignmentReceipt): Promise<void> {
    if (!this.canPost(r) || this.busyReceiptId()) return;
    const ok = await this.confirm.ask({
      title: `Post receipt #${r.sequence}`,
      body: 'The accepted quantity moves from Transit into the destination stock. Damaged, rejected and missing quantities stay in transit. This cannot be undone here.',
      confirmLabel: 'Post receipt',
    });
    if (!ok) return;
    let sub = this.postSubmissions.get(r.id);
    if (!sub) this.postSubmissions.set(r.id, (sub = new IdempotentSubmission()));
    const submission = sub;
    this.busyReceiptId.set(r.id);
    this.actionError.set(null);
    this.service.postReceipt(this.consignment()!.id, r.id, submission.key).subscribe({
      next: () => {
        this.busyReceiptId.set(null);
        submission.succeeded();
        this.toast.show('Receipt posted.');
        this.refresh();
      },
      error: (err) => {
        this.busyReceiptId.set(null);
        submission.failed(err);
        this.actionError.set(plannerProblemMessage(toProblem(err)));
      },
    });
  }
}
