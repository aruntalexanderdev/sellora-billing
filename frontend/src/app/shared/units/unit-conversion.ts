/**
 * Pure unit-conversion maths for product unit hierarchies.
 *
 * Mirrors the backend `ProductUnitGraph` / `ProductUnitConfigurationValidator` rules (Phase 2 §5.1)
 * so the UI can give instant feedback. The server stays the authority: it computes and stores
 * `ConversionFactorToBase`, and transactions read the stored factor. Components must never
 * hand-roll factor arithmetic; use these functions.
 *
 * Every multiplication/division result is passed through {@link round10} to strip binary
 * floating-point noise (e.g. 0.1 * 3 = 0.30000000000000004).
 */
import type { MeasurementCategory } from '../../features/masters/units/units.models';

/** Smallest stored factor allowed (R6). */
export const MIN_FACTOR = 1e-6;
/** Largest stored factor / inner quantity allowed (R6). */
export const MAX_FACTOR = 1e9;
/** Max decimals for `InnerQuantity` (numeric(18,6)). */
export const INNER_QUANTITY_DECIMALS = 6;

/** Rounds to 10 decimals, removing floating-point noise (matches numeric(28,10) factor storage). */
export function round10(value: number): number {
  return Number(value.toFixed(10));
}

/** True when `value` has at most `decimals` fractional digits. */
export function hasAtMostDecimals(value: number, decimals: number): boolean {
  if (!Number.isFinite(value)) return false;
  const scaled = round10(value * 10 ** decimals);
  return Number.isInteger(scaled);
}

// ---------------------------------------------------------------------------
// Factor graph
// ---------------------------------------------------------------------------

/** A node of a product unit graph. `innerKey` is null only for the base row. */
export interface FactorNode {
  key: string;
  innerKey: string | null;
  /** How many inner units one of this unit contains. Ignored for the base row. */
  innerQuantity: number | null;
}

/**
 * - `cycle`: the row is part of an inner-unit cycle.
 * - `orphan`: the row's walk does not reach the base (missing inner, or the inner itself is broken).
 * - `invalidQuantity`: the row (or a row on its walk) has no positive inner quantity.
 */
export type FactorError = 'cycle' | 'orphan' | 'invalidQuantity';

export interface FactorResult {
  /** Conversion factor to base; null when `error` is set. */
  factor: number | null;
  /** 0 for base, inner depth + 1 otherwise; null when `error` is set. */
  depth: number | null;
  error: FactorError | null;
}

/**
 * Computes `factor(base) = 1`, `factor(u) = u.innerQuantity × factor(u.inner)` for every row,
 * with cycle and orphan detection (DFS with visiting/done states, like the backend).
 */
export function computeFactors(rows: readonly FactorNode[], baseKey: string): Map<string, FactorResult> {
  const byKey = new Map<string, FactorNode>();
  for (const row of rows) byKey.set(row.key, row);

  const done = new Map<string, FactorResult>();
  const visiting = new Set<string>();

  const fail = (error: FactorError): FactorResult => ({ factor: null, depth: null, error });

  const visit = (key: string): FactorResult => {
    const memo = done.get(key);
    if (memo) return memo;
    const row = byKey.get(key);
    if (!row) return fail('orphan');

    if (key === baseKey) {
      const base: FactorResult = { factor: 1, depth: 0, error: null };
      done.set(key, base);
      return base;
    }
    if (visiting.has(key)) return fail('cycle');
    if (row.innerKey === null || row.innerKey === key || !byKey.has(row.innerKey)) {
      const result = row.innerKey === key ? fail('cycle') : fail('orphan');
      done.set(key, result);
      return result;
    }

    visiting.add(key);
    const inner = visit(row.innerKey);
    visiting.delete(key);

    let result: FactorResult;
    if (inner.error !== null) {
      // Members of the cycle are flagged 'cycle' when the walk comes back to them; anything that only
      // leads into a broken chain is an orphan (it cannot reach the base).
      if (inner.error === 'cycle' && isOnCycle(byKey, key)) result = fail('cycle');
      else result = fail(inner.error === 'invalidQuantity' ? 'invalidQuantity' : 'orphan');
    } else if (row.innerQuantity === null || !(row.innerQuantity > 0)) {
      result = fail('invalidQuantity');
    } else {
      result = { factor: round10(row.innerQuantity * inner.factor!), depth: inner.depth! + 1, error: null };
    }
    done.set(key, result);
    return result;
  };

  const results = new Map<string, FactorResult>();
  for (const row of rows) results.set(row.key, visit(row.key));
  return results;
}

/** True when walking inner links from `key` returns to `key`. */
function isOnCycle(byKey: Map<string, FactorNode>, key: string): boolean {
  const seen = new Set<string>();
  let current = byKey.get(key)?.innerKey ?? null;
  while (current !== null && !seen.has(current)) {
    if (current === key) return true;
    seen.add(current);
    current = byKey.get(current)?.innerKey ?? null;
  }
  return false;
}

/**
 * Keys of every row whose inner chain passes through `key` (its "outer" rows, transitively).
 * Used to prevent picking an inner that would create a cycle.
 */
export function outerKeys(rows: readonly FactorNode[], key: string): Set<string> {
  const result = new Set<string>();
  let frontier = [key];
  while (frontier.length > 0) {
    const next: string[] = [];
    for (const row of rows) {
      if (row.innerKey !== null && frontier.includes(row.innerKey) && !result.has(row.key) && row.key !== key) {
        result.add(row.key);
        next.push(row.key);
      }
    }
    frontier = next;
  }
  return result;
}

// ---------------------------------------------------------------------------
// Rules mirrored from the validator
// ---------------------------------------------------------------------------

/**
 * R3 (D9): may a unit of `unit` category sit in a hierarchy whose base is `base` category?
 * Packaging wraps anything; Weight/Volume/Length must equal the base category;
 * Count is allowed only over a Count base. A Packaging base is never valid (R1).
 */
export function isCategoryAllowed(base: MeasurementCategory, unit: MeasurementCategory): boolean {
  if (base === 'Packaging') return false;
  if (unit === 'Packaging') return true;
  return unit === base;
}

interface StandardFactorCarrier {
  standardFactor: number | null;
}

/**
 * R4: when both units carry a StandardFactor, the inner quantity is fixed by physics
 * (`unit.SF / inner.SF`, 6 dp). Returns null when either factor is missing.
 */
export function suggestInnerQuantity(unit: StandardFactorCarrier, inner: StandardFactorCarrier): number | null {
  if (unit.standardFactor === null || inner.standardFactor === null) return null;
  if (!(unit.standardFactor > 0) || !(inner.standardFactor > 0)) return null;
  return Number((unit.standardFactor / inner.standardFactor).toFixed(INNER_QUANTITY_DECIMALS));
}

/**
 * R5 (generalised whole-number rule): `factor × 10^baseDecimalPlaces` must be an integer, so one of
 * the unit always converts to a storable base quantity. A 0-dp base needs integer factors;
 * a 3-dp kg base accepts 0.001 but rejects 0.0005.
 */
export function isWholeForBase(factor: number, baseDecimalPlaces: number): boolean {
  return hasAtMostDecimals(factor, baseDecimalPlaces);
}

/** R6: factor range check. */
export function isFactorInRange(factor: number): boolean {
  return factor >= MIN_FACTOR && factor <= MAX_FACTOR;
}

/** R6: `0 < innerQuantity ≤ 1e9` with at most 6 decimals. */
export function isValidInnerQuantity(qty: number | null | undefined): boolean {
  return typeof qty === 'number' && qty > 0 && qty <= MAX_FACTOR && hasAtMostDecimals(qty, INNER_QUANTITY_DECIMALS);
}

// ---------------------------------------------------------------------------
// Conversion and display
// ---------------------------------------------------------------------------

/** `qty × fromFactor / toFactor`, both factors relative to the same base. */
export function convert(qty: number, fromFactor: number, toFactor: number): number {
  return round10((qty * fromFactor) / toFactor);
}

/** Formats a quantity for chain text without float noise (no fixed decimals). */
export function formatChainQuantity(value: number): string {
  return String(round10(value));
}

/** A graph node that also carries its display name. */
export interface ChainNode extends FactorNode {
  name: string;
}

/**
 * Equivalence chain from `key` down to the base, e.g. "1 Full Carton = 2 Half Carton = 10 Box = 100 Piece".
 * Returns null when the walk cannot reach a base (cycle / orphan / missing quantity).
 */
export function equivalenceChain(rows: readonly ChainNode[], key: string): string | null {
  const byKey = new Map(rows.map((r) => [r.key, r] as const));
  let current = byKey.get(key);
  if (!current) return null;

  const parts = [`1 ${current.name}`];
  let qty = 1;
  const seen = new Set<string>([key]);
  while (current.innerKey !== null) {
    if (current.innerQuantity === null || !(current.innerQuantity > 0)) return null;
    const inner = byKey.get(current.innerKey);
    if (!inner || seen.has(inner.key)) return null;
    qty = round10(qty * current.innerQuantity);
    parts.push(`${formatChainQuantity(qty)} ${inner.name}`);
    seen.add(inner.key);
    current = inner;
  }
  return parts.join(' = ');
}

export interface BreakdownUnit {
  key: string;
  factor: number;
}

export interface BreakdownPart {
  key: string;
  quantity: number;
}

export interface Breakdown {
  parts: BreakdownPart[];
  /** Base quantity that no unit could absorb (0 when a factor-1 unit is included). */
  remainder: number;
}

/**
 * Greedy nominal breakdown of a base quantity, largest factor first
 * (122 pc over FCtn 100 / HCtn 50 / Box 10 / pc 1 → 1 FCtn, 2 Box, 2 pc). Zero parts are omitted.
 */
export function breakdown(baseQty: number, units: readonly BreakdownUnit[]): Breakdown {
  const sorted = [...units].filter((u) => u.factor > 0).sort((a, b) => b.factor - a.factor);
  const parts: BreakdownPart[] = [];
  let remaining = round10(baseQty);
  for (const unit of sorted) {
    if (remaining <= 0) break;
    const count = Math.floor(round10(remaining / unit.factor));
    if (count > 0) {
      parts.push({ key: unit.key, quantity: count });
      remaining = round10(remaining - count * unit.factor);
    }
  }
  return { parts, remainder: remaining };
}

// ---------------------------------------------------------------------------
// Quantity formatting (Phase 3 minimal formatter; Phase 7's QuantityPipe wraps it)
// ---------------------------------------------------------------------------

/** Max decimals any quantity is stored with (numeric(18,3)). */
export const QUANTITY_STORAGE_DECIMALS = 3;

/**
 * Formats a quantity with the unit's `decimalPlaces` (clamped 0–3), without float noise:
 * `formatQuantity(300, 0)` → "300", `formatQuantity(1.5, 3)` → "1.500".
 *
 * Never hides stored precision: a value carrying more decimals than the unit allows (legacy data)
 * shows up to 3 decimals instead of being silently rounded (`formatQuantity(2.5, 0)` → "2.5").
 */
export function formatQuantity(value: number, decimalPlaces: number | null | undefined): string {
  if (!Number.isFinite(value)) return '—';
  const raw = Math.trunc(decimalPlaces ?? 0);
  const dp = Math.min(QUANTITY_STORAGE_DECIMALS, Math.max(0, Number.isFinite(raw) ? raw : 0));
  const clean = round10(value);
  let decimals = dp;
  while (decimals < QUANTITY_STORAGE_DECIMALS && !hasAtMostDecimals(clean, decimals)) decimals++;
  const text = clean.toFixed(decimals);
  // Avoid "-0" / "-0.000".
  return Number(text) === 0 ? (0).toFixed(decimals) : text;
}

/** Cost per base unit given a cost per entered unit with `factor` base units (money; not rounded here). */
export function costPerBaseUnit(costPerEnteredUnit: number, factor: number): number {
  return convert(costPerEnteredUnit, 1, factor);
}
