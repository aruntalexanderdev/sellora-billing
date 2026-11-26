import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import { toProblem, problemMessage } from '../../../../core/http/api-errors';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { formatChainQuantity, formatQuantity } from '../../../../shared/units/unit-conversion';
import { ReasonPrompt } from '../../../administration/access-ui/reason-prompt';
import { loadErrorText, problemText } from '../../../../shared/scope/problem-text';
import { TransferReceiveDialog } from '../transfer-dialogs/transfer-receive-dialog';
import { TransferResolveDialog } from '../transfer-dialogs/transfer-resolve-dialog';
import { TransferApproveDialog } from '../transfer-dialogs/transfer-approve-dialog';
import { IdempotentSubmission, availableActions, baseToUnitQty, closeProblemMessage, endpointLabel, formatBase, statusLabel, statusTone, type TransferAction } from '../stock-transfer-rules';
import { StockTransfersService } from '../stock-transfers.service';
import type { StockTransfer, StockTransferActor, StockTransferHistoryEntry, StockTransferLine } from '../stock-transfers.models';

type Panel = 'approve' | 'reject' | 'requesterReject' | 'cancel' | 'close' | 'receive' | 'resolve';

/** Converted display of a base quantity in the line's request unit; '' when not exactly representable. */
function lineUnitDisplay(l: StockTransferLine, baseQty: number): string {
  if (l.conversionFactorToBase === 1) return '';
  const qty = baseToUnitQty(baseQty, { decimalPlaces: 3, conversionFactorToBase: l.conversionFactorToBase });
  return qty === null ? '' : `${formatQuantity(qty, 0)} ${l.unitCode}`;
}

interface TimelineEntry {
  label: string;
  actor: StockTransferActor;
  note?: string | null;
}

@Component({
  selector: 'app-stock-transfer-detail',
  standalone: true,
  imports: [RouterLink, DatePipe, DecimalPipe, UiBadge, UiButton, ReasonPrompt, TransferReceiveDialog, TransferResolveDialog, TransferApproveDialog],
  templateUrl: './stock-transfer-detail.html',
  styleUrl: './stock-transfer-detail.scss',
})
export class StockTransferDetail implements OnInit {
  private readonly service = inject(StockTransfersService);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly submissions = new Map<string, IdempotentSubmission>();

  readonly transfer = signal<StockTransfer | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly panel = signal<Panel | null>(null);
  /** Legacy direct dispatch is gone (FI-D34): approved requests are dispatched through a shipment. */
  readonly shipmentDispatchHint = computed(() => {
    const s = this.transfer()?.status;
    return s === 'Approved' || s === 'PartiallyFulfilled';
  });
  /** Pending approval that this user cannot decide here: say who decides (and why not you) instead of showing nothing. */
  readonly awaitingApprovalBy = computed<string | null>(() => {
    const t = this.transfer();
    if (!t) return null;
    // FI-D48 level 1: the requesting (destination) branch confirms a high-value request first.
    const level1 = t.status === 'AwaitingRequesterApproval';
    if (!level1 && t.status !== 'Requested') return null;
    if (level1 ? this.can('requesterApprove') : this.can('approve')) return null;
    const branch = level1 ? t.destinationBranch : t.sourceBranch;
    const by = branch.name || branch.code || 'a company-level approver';
    const who = t.approvalBlockedForYou
      ? `another approver of ${by} (${level1 || !t.requesterApproved ? 'you created or submitted it' : 'you created, submitted or approved it for the requesting branch'})`
      : by;
    return level1 ? `${who} — the branch that requested the stock confirms high-value requests first.` : `${who} — the branch that supplies the stock decides.`;
  });
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);

  readonly actions = computed<TransferAction[]>(() => {
    const t = this.transfer();
    return t ? availableActions(t, (c) => this.auth.hasPermission(c)) : [];
  });
  readonly can = (a: TransferAction) => this.actions().includes(a);

  readonly timeline = computed<TimelineEntry[]>(() => {
    const t = this.transfer();
    if (!t) return [];
    const entries: (TimelineEntry | null)[] = [
      { label: 'Created', actor: t.created },
      t.submitted ? { label: 'Submitted', actor: t.submitted } : null,
      t.requesterApproved ? { label: 'Approved by the requesting branch', actor: t.requesterApproved } : null,
      t.approved ? { label: 'Approved', actor: t.approved, note: t.approvalAdjustmentReason ?? null } : null,
      t.rejected ? { label: 'Rejected', actor: t.rejected, note: t.rejectionReason } : null,
      t.dispatched ? { label: 'Dispatched', actor: t.dispatched } : null,
      t.completed ? { label: 'Completed', actor: t.completed } : null,
      t.cancelled ? { label: 'Cancelled', actor: t.cancelled, note: t.cancellationReason } : null,
      t.closedAt ? { label: 'Closed (short)', actor: { userId: null, userName: t.closedByName, at: t.closedAt }, note: t.closureReason } : null,
    ];
    return entries.filter((e): e is TimelineEntry => e !== null);
  });

  readonly history = signal<StockTransferHistoryEntry[]>([]);
  readonly historyError = signal<string | null>(null);

  readonly tone = statusTone;
  readonly statusText = statusLabel;
  readonly cost = (n: number | null) => (n === null || n === undefined ? '—' : String(n));
  /** Base quantity shown in the line's unit (e.g. "= 3 box") when it converts exactly; empty otherwise. */
  readonly inLineUnit = (l: StockTransferLine, baseQty: number) => {
    const u = lineUnitDisplay(l, baseQty);
    return u ? `≈ ${u}` : '';
  };
  readonly label = endpointLabel;
  readonly factor = (n: number) => formatChainQuantity(n);
  readonly base = (l: StockTransferLine, n: number) => formatBase(l, n);
  readonly entered = (n: number | null) => (n === null ? '—' : formatQuantity(n, 0));
  /** Entered quantities carry no unit precision in the DTO: shown without padding (decimals appear only when present). */
  readonly lineName = (t: StockTransfer, lineId: string) => t.lines.find((l) => l.id === lineId)?.productName ?? 'Line';
  readonly rbase = (t: StockTransfer, lineId: string, n: number) => {
    const l = t.lines.find((x) => x.id === lineId);
    return `${l ? formatBase(l, n) : n} ${l?.baseUnitCode ?? ''}`.trim();
  };
  readonly dispatched = (l: StockTransferLine) => l.dispatchedQty !== null;

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.service.get(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (t) => {
        this.transfer.set(t);
        this.loading.set(false);
        this.loadHistory();
      },
      error: (err) => {
        this.loadError.set(loadErrorText(err, 'Could not load this transfer.'));
        this.loading.set(false);
      },
    });
  }

  loadHistory(): void {
    this.historyError.set(null);
    this.service.history(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (h) => this.history.set(h),
      error: (err) => this.historyError.set(problemText(err, 'Could not load the history.')),
    });
  }

  open(panel: Panel): void {
    this.actionError.set(null);
    this.panel.set(panel);
  }

  close(): void {
    const t = this.transfer();
    const open = this.panel();
    this.panel.set(null);
    // An ambiguous failure (network / timeout / 5xx) may have been processed: show current quantities. The key is kept.
    if (t && open) {
      const sub = this.submissionFor(open);
      if (sub.hasAmbiguousFailure) {
        sub.acknowledged();
        this.refresh();
      }
    }
    this.actionError.set(null);
  }

  /** A dialog or action finished: show the server's new state. */
  onUpdated(t: StockTransfer, message?: string): void {
    this.transfer.set(t);
    this.loadHistory();
    this.panel.set(null);
    this.actionError.set(null);
    if (message) this.toast.show(message);
  }

  edit(): void {
    this.router.navigate(['/inventory/stock-transfers', this.transfer()!.id, 'edit']);
  }

  submit(): void {
    this.run('submit', (id, key) => this.service.submit(id, key), 'Transfer submitted.');
  }

  approve(): void {
    this.open('approve');
  }

  requesterApprove(): void {
    this.run('requesterApprove', (id, key) => this.service.requesterApprove(id, key), 'Request approved for the requesting branch.');
  }

  confirmRequesterReject(reason: string | null): void {
    if (!reason) return;
    this.run('requesterReject', (id, key) => this.service.requesterReject(id, reason, key), 'Request rejected.');
  }

  confirmReason(kind: 'reject' | 'cancel', reason: string | null): void {
    if (!reason) return;
    this.run(kind, (id, key) => (kind === 'reject' ? this.service.reject(id, reason, key) : this.service.cancel(id, reason, key)), kind === 'reject' ? 'Transfer rejected.' : 'Transfer cancelled.');
  }

  confirmClose(reason: string | null): void {
    if (!reason) return;
    this.run('close', (id, key) => this.service.close(id, reason, key), 'Remaining quantity closed.');
  }

  /** The submission for (this transfer, action); kept for the page's lifetime so reopening a dialog replays the same key. */
  submissionFor(name: string): IdempotentSubmission {
    const id = this.transfer()?.id ?? this.route.snapshot.paramMap.get('id') ?? '';
    const k = `${id}:${name}`;
    let submission = this.submissions.get(k);
    if (!submission) this.submissions.set(k, (submission = new IdempotentSubmission()));
    return submission;
  }

  /** Re-reads the transfer without the loading state (the page stays visible). */
  private refresh(): void {
    this.service.get(this.route.snapshot.paramMap.get('id')!).subscribe({ next: (t) => this.transfer.set(t) });
  }

  private run(name: string, call: (id: string, key: string) => ReturnType<StockTransfersService['submit']>, success: string): void {
    const t = this.transfer();
    if (!t || this.busy()) return;
    const sub = this.submissionFor(name);
    this.busy.set(true);
    this.actionError.set(null);
    call(t.id, sub.key).subscribe({
      next: (updated) => {
        this.busy.set(false);
        sub.succeeded();
        this.onUpdated(updated, success);
      },
      error: (err) => {
        this.busy.set(false);
        sub.failed(err);
        this.actionError.set((name === 'close' ? closeProblemMessage(toProblem(err)) : null) ?? problemText(err, problemMessage(toProblem(err))));
      },
    });
  }
}
