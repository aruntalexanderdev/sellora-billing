---
name: angular-conventions
description: Sellora Angular 22 frontend conventions — standalone components, signals, reactive forms, feature folder layout, services/models, shared UI kit, error mapping, routing/permissions, quantity display and Vitest testing. Load before writing or reviewing frontend code.
user-invocable: false
---

# Angular conventions (derived from existing code)

## Layout
```
frontend/src/app/
  core/            auth (guard, interceptor, service), idempotency.ts, unsaved-changes.guard.ts, api.models.ts
  layout/          shell, header, sidebar
  shared/ui/       badge, button, card, confirm-dialog, data-table, toast
  features/<area>/<feature>/
      <feature>.models.ts     TS interfaces mirroring backend DTOs (camelCase)
      <feature>.service.ts    @Injectable({providedIn:'root'}), HttpClient, BASE_URL from environment.apiBaseUrl
      <name>-list/ <name>-form/ <name>-detail/   component .ts + .html (+ scss)
```
Routes in `app.routes.ts` with permission guards (e.g. `Masters.Edit`, `Inventory.Price`).

## Component pattern (see `features/masters/units/unit-form/unit-form.ts`)
- `standalone: true`, explicit `imports`, `inject()` for DI (no constructor injection).
- State with `signal`/`computed`; forms via `fb.nonNullable.group`, `FormArray` for line items.
- Forms implement `HasUnsavedChanges` when editable.
- Server errors: map `ApiProblem.errors[field]` onto the matching control; fallback `serverError` signal + `ToastService`.
- Create endpoints that support idempotency send the key from `core/idempotency.ts`.
- Confirmations via `ConfirmDialogService`.

## Units & quantities (restructuring rules)
- One pure util `shared/units/unit-conversion.ts` (factor calc, convert, breakdown, validation mirror). Components never hand-roll conversion maths.
- One `QuantityPipe` (precision from unit `decimalPlaces`); no `toFixed(2)` for quantities.
- Quantity inputs: `step` and `Validators` derived from the selected unit's `decimalPlaces`.
- Unit selectors list only active + purchasable/sellable product units; preselect default purchase/sales unit.
- Keep `StockMovementType` and other string unions in sync with backend enums.

## Testing
Vitest via `ng test` (`npm --prefix frontend test`); specs beside the file (`*.spec.ts`). Priority: conversion util, quantity pipe, product hierarchy editor, POS unit selection.

## Build
```bash
npm --prefix frontend run build
```
No new UI or state-management libraries.
