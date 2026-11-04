---
name: backend-conventions
description: Sellora .NET backend coding conventions — layering, services, controllers, DTO records, validation errors, permissions, idempotency, transactions and testing style. Load before writing or reviewing backend C# code.
user-invocable: false
---

# Backend conventions (derived from existing code)

## Layering
| Layer | Put here | Example |
|---|---|---|
| `Sellora.Domain/Entities` | POCO entities, enums; XML doc comments explain business intent | `PurchaseBatch.cs` |
| `Sellora.Domain/Abstractions` | `I*Service` interfaces, `DomainValidationException` | `ISalesService.cs` |
| `Sellora.Domain/<Module>` | input/result models (records) | `Sales/SalesModels.cs` |
| `Sellora.Infrastructure/Services` | write logic, primary-constructor DI `(SelloraDbContext db)` | `SalesService.cs` |
| `Sellora.Api/Controllers` | `[ApiController]`, `[Route("api/v1/...")]`, policy auth, DTO mapping, read/report LINQ | `StockController.cs` |
| `Sellora.Api/Contracts` | positional `record` DTOs with DataAnnotations | `ProductContracts.cs` |

No MediatR, repositories or AutoMapper — keep to this shape.

## Patterns
- Validation: request DataAnnotations → `ValidationProblem(ModelState)`; business rules throw `new DomainValidationException("<RequestFieldName>", "Human message.")` → 400 ValidationProblemDetails via `GlobalExceptionHandler` (field name must match the Angular control/DTO property).
- Not found → `NotFound()`; concurrency/state conflict → 409 ProblemDetails.
- Authorization: class-level `[Authorize(Policy = "<Area>.View")]`, action-level for writes. New permissions go in `Sellora.Domain/Permissions/PermissionCatalog.cs` (`new("Area.Action", "Group", "Label", "Description")`) — e.g. `Sales.PriceOverride`.
- Stock writes: see `stock-ledger-invariants` (transaction, locks, idempotency, EnsureStorable).
- Document numbers: `StockLocks.LockDocumentNumberAsync` + row-count numbering.
- Idempotent creates: `IdempotencyHeader.TryRead` in controller, `Idempotency.FindAsync/Record` in service.
- Money: line values `numeric(18,4)`, header totals `numeric(18,2)`; multi-currency via `ExchangeRateToBase`.
- Async everywhere with `CancellationToken ct`.
- Strings trimmed on input; uniqueness checks case-insensitive in service.

## Tests
xUnit, one class per service under `backend/tests/Sellora.Tests/<Module>/`, `UseInMemoryDatabase(Guid.NewGuid().ToString())`, private `SeedAsync()` fixture (move to shared builders as introduced), names `Method_Scenario_Expectation`.

## Build/test
```bash
dotnet build backend/Sellora.slnx
```
```bash
dotnet test backend/Sellora.slnx
```
