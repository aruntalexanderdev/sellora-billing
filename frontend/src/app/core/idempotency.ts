import { HttpHeaders } from '@angular/common/http';

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
