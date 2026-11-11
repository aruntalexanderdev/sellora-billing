import { firstValueFrom } from 'rxjs';
import { IdempotentSubmission, actionPermission, baseToUnitQty, lineUnitId } from '../../inventory/stock-transfers/stock-transfer-rules';
import type { StockTransfersService } from '../../inventory/stock-transfers/stock-transfers.service';
import type { CreateStockTransferRequest, StockTransfer } from '../../inventory/stock-transfers/stock-transfers.models';

/** D11 / O6: create(submit) -> approve -> dispatch -> receive, as far as the user's permissions allow. */
export type ChainStep = 'create' | 'submit' | 'approve' | 'dispatch' | 'receive';

/** One Idempotency-Key per step (rotated by `keyFor` when that step's body changes); the set lives for one wizard submission and is reused when a stopped chain is retried. */
export class ChainKeys {
  readonly create = new IdempotentSubmission();
  readonly submit = new IdempotentSubmission();
  readonly approve = new IdempotentSubmission();
  readonly dispatch = new IdempotentSubmission();
  readonly receive = new IdempotentSubmission();
}

export interface ChainResult {
  /** Latest known state of the transfer (null when create did not happen). */
  transfer: StockTransfer | null;
  /** Steps that succeeded in this run. */
  completed: ChainStep[];
  /** Step the chain stopped before / at; null = reached Completed. */
  stoppedAt: ChainStep | null;
  reason: 'permission' | 'error' | 'status' | null;
  /** Permission code the user lacks (reason = permission). */
  missingPermission: string | null;
  /** Raw HTTP error (reason = error). */
  error: unknown;
}

type Has = (code: string) => boolean;

function stop(transfer: StockTransfer | null, completed: ChainStep[], step: ChainStep, reason: ChainResult['reason'], extra: Partial<ChainResult> = {}): ChainResult {
  return { transfer, completed, stoppedAt: step, reason, missingPermission: null, error: null, ...extra };
}

/**
 * Runs the chain. Pass `existing` to resume a transfer that a previous run left part-way. Stops at the first step the user
 * has no permission for (`reason: permission`) or the first server rejection (`reason: error`); the caller links to the transfer.
 */
export async function runVanChain(
  service: Pick<StockTransfersService, 'create' | 'submit' | 'approve' | 'dispatch' | 'receive'>,
  request: Omit<CreateStockTransferRequest, 'submit'> | null,
  existing: StockTransfer | null,
  has: Has,
  keys: ChainKeys,
): Promise<ChainResult> {
  const completed: ChainStep[] = [];
  let t = existing;
  let step: ChainStep = 'create';

  const attempt = async (name: ChainStep, submission: IdempotentSubmission, call: () => Promise<StockTransfer>): Promise<ChainResult | null> => {
    step = name;
    try {
      t = await call();
      submission.succeeded();
      completed.push(name);
      return null;
    } catch (error) {
      submission.failed(error);
      return stop(t, completed, name, 'error', { error });
    }
  };
  const needs = (name: ChainStep, action: Parameters<typeof actionPermission>[0]): ChainResult | null => {
    const code = actionPermission(action);
    return has(code) ? null : stop(t, completed, name, 'permission', { missingPermission: code });
  };

  if (!t) {
    const blocked = needs('create', 'submit');
    if (blocked) return blocked;
    if (!request) return stop(null, completed, 'create', 'status');
    const failed = await attempt('create', keys.create, () => firstValueFrom(service.create({ ...request, submit: true }, keys.create.keyFor({ ...request, submit: true }))));
    if (failed) return failed;
  }

  // Bounded loop: at most one step per status transition.
  for (let guard = 0; guard < 6 && t; guard++) {
    const current: StockTransfer = t;
    let result: ChainResult | null;
    switch (current.status) {
      case 'Draft':
        result = needs('submit', 'submit') ?? (await attempt('submit', keys.submit, () => firstValueFrom(service.submit(current.id, keys.submit.key))));
        break;
      case 'Requested':
        result = needs('approve', 'approve') ?? (await attempt('approve', keys.approve, () => firstValueFrom(service.approve(current.id, keys.approve.key))));
        break;
      case 'Approved':
        result =
          needs('dispatch', 'dispatch') ??
          (await attempt('dispatch', keys.dispatch, () => {
            // Whole request as asked: every line, requested unit and quantity.
            const dispatchBody = { lines: current.lines.map((l) => ({ lineId: l.id, productUnitId: l.productUnitId, quantity: l.requestedQty })) };
            return firstValueFrom(service.dispatch(current.id, dispatchBody, keys.dispatch.keyFor(dispatchBody)));
          }));
        break;
      case 'Dispatched':
      case 'PartiallyReceived': {
        const blocked = needs('receive', 'receive');
        if (blocked) {
          result = blocked;
          break;
        }
        const lines = current.lines.filter((l) => l.outstandingBaseQty > 0);
        const body = lines.map((l) => ({
          lineId: l.id,
          productUnitId: lineUnitId(l),
          // Everything that arrived, in the unit it was dispatched in.
          acceptedQuantity:
            l.outstandingBaseQty === l.dispatchedBaseQty && l.dispatchedQty !== null
              ? l.dispatchedQty
              : baseToUnitQty(l.outstandingBaseQty, { decimalPlaces: 3, conversionFactorToBase: l.dispatchFactor ?? l.conversionFactorToBase }),
          damagedQuantity: 0,
        }));
        if (body.some((b) => b.acceptedQuantity === null)) {
          result = stop(t, completed, 'receive', 'status');
          break;
        }
        result = await attempt('receive', keys.receive, async () => {
          const receiveBody = { notes: null, lines: body.map((b) => ({ ...b, acceptedQuantity: b.acceptedQuantity! })) };
          const res = await firstValueFrom(service.receive(current.id, receiveBody, keys.receive.keyFor(receiveBody)));
          return res.transfer;
        });
        break;
      }
      case 'Completed':
        return { transfer: t, completed, stoppedAt: null, reason: null, missingPermission: null, error: null };
      default:
        // Rejected / Cancelled: nothing more the wizard can do.
        return stop(t, completed, step, 'status');
    }
    if (result) return result;
  }
  return stop(t, completed, step, 'status');
}
