import { DatePipe } from '@angular/common';
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
import { TransferDispatchDialog } from '../transfer-dialogs/transfer-dispatch-dialog';
import { TransferReceiveDialog } from '../transfer-dialogs/transfer-receive-dialog';
import { TransferResolveDialog } from '../transfer-dialogs/transfer-resolve-dialog';
import { IdempotentSubmission, availableActions, baseToUnitQty, closeProblemMessage, endpointLabel, formatBase, statusLabel, statusTone, type TransferAction } from '../stock-transfer-rules';
import { StockTransfersService } from '../stock-transfers.service';
import type { StockTransfer, StockTransferActor, StockTransferHistoryEntry, StockTransferLine } from '../stock-transfers.models';

type Panel = 'reject' | 'cancel' | 'close' | 'dispatch' | 'receive' | 'resolve';

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
  imports: [RouterLink, DatePipe, UiBadge, UiButton, ReasonPrompt, TransferDispatchDialog, TransferReceiveDialog, TransferResolveDialog],
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
      t.approved ? { label: 'Approved', actor: t.approved } : null,
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
    this.run('approve', (id, key) => this.service.approve(id, key), 'Transfer approved.');
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
