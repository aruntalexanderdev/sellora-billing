import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';

/**
 * A fresh key per form instance. Sent as the Idempotency-Key header on create requests, so a double-click or a
 * retry after a timeout returns the document the first request created instead of creating (and moving stock for)
 * a second one.
 */
export function newIdempotencyKey(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}-${Math.random().toString(36).slice(2)}`;
}

export function idempotencyHeaders(key: string | undefined): HttpHeaders | undefined {
  return key ? new HttpHeaders({ 'Idempotency-Key': key }) : undefined;
}

/**
 * One Idempotency-Key per submission. The key stays the same for retries of the *same* request (network error, 5xx,
 * timeout - the first attempt may have been processed) and rotates once the submission is finished (success), definitively
 * rejected (400/403/404/422: nothing was posted and the user is about to change the body), or the request body / location
 * changed since the previous attempt (`keyFor`: same fingerprint -> same key, different -> a new key).
 */
export class IdempotentSubmission {
  private current = newIdempotencyKey();
  private fingerprint: string | null = null;
  private ambiguous = false;

  get key(): string {
    return this.current;
  }

  /** True after a failure whose outcome is unknown (network / timeout / 5xx); the server may have processed it. */
  get hasAmbiguousFailure(): boolean {
    return this.ambiguous;
  }

  /** Key for a request with this body: reused for an identical retry, rotated when the body differs from the last attempt. */
  keyFor(body: unknown): string {
    const fp = JSON.stringify(body) ?? '';
    if (this.fingerprint !== null && this.fingerprint !== fp) this.current = newIdempotencyKey();
    this.fingerprint = fp;
    return this.current;
  }

  succeeded(): void {
    this.rotate();
  }

  failed(error: unknown): void {
    if (error instanceof HttpErrorResponse && [400, 403, 404, 422].includes(error.status)) {
      this.rotate();
      return;
    }
    // 409 keeps the key too: the server is idempotent by state, so a replay is safe.
    this.ambiguous = true;
  }

  /** The caller has re-read the server state after an ambiguous failure. */
  acknowledged(): void {
    this.ambiguous = false;
  }

  private rotate(): void {
    this.current = newIdempotencyKey();
    this.fingerprint = null;
    this.ambiguous = false;
  }
}
