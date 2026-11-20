# Sellora Billing

Sellora Billing is a billing and business management platform for retail shops, small/medium businesses and general trading businesses — covering purchasing, sales, inventory/stock, multi-currency accounts, expenses and financial reporting.

## Tech Stack

| Layer      | Technology |
|------------|------------|
| Backend    | C# / ASP.NET Core Web API (.NET 10), EF Core |
| Database   | PostgreSQL |
| Frontend   | Angular 22, TypeScript, RxJS |
| Auth       | JWT with role/permission-based authorization (RBAC) |
| Testing    | xUnit (backend) |

## Project Structure

```
backend/
  src/
    Sellora.Api/             API controllers, DTOs, authorization, error handling
    Sellora.Domain/          Entities, service interfaces, domain models
    Sellora.Infrastructure/  EF Core DbContext, migrations, service implementations
  tests/
    Sellora.Tests/           xUnit tests, mirrors domain modules

frontend/
  src/app/
    core/                    Auth guard/interceptor/service
    layout/                  Shell, header, sidebar
    shared/ui/                Reusable UI components (button, card, data-table, toast...)
    features/                Feature modules (sales, purchases, inventory, accounts, administration, reports...)

docs/                        Design and phase-planning references
```

## Key Modules

- **Sales** — POS, sales invoices, returns, customers
- **Purchasing** — purchase orders, batch traceability, returns, suppliers
- **Inventory** — products, pricing, stock, stock adjustments
- **Accounts** — cash/bank accounts, expenses, financial transactions, reports
- **Administration** — users, roles, permissions, currencies, exchange rates
- **Reports** — sales, purchase, stock movement, profit summary, accounts reports

## Getting Started

### Prerequisites
- .NET 10 SDK
- Node.js + npm
- PostgreSQL (running locally or accessible)

### Backend
```bash
cd backend

# copy the example config and fill in your local DB connection string + a JWT signing key
cp src/Sellora.Api/appsettings.Development.json.example src/Sellora.Api/appsettings.Development.json

dotnet restore
dotnet ef database update --project src/Sellora.Infrastructure --startup-project src/Sellora.Api
dotnet run --project src/Sellora.Api
```

### Frontend
```bash
cd frontend
npm install
npm start
```
App runs on `http://localhost:4200` by default, proxying API calls to the backend.

### Running Tests
```bash
cd backend
dotnet test
```

## Notes for Contributors
- Backend follows a service-oriented architecture (`Controller → Service → EF Core → PostgreSQL`) — no Repository pattern by default.
- `appsettings.Development.json` is git-ignored; always start from `appsettings.Development.json.example`.
- Local startup seeds demo masters (units, taxes, products, categories, suppliers, customers, expense categories) into the default company. Set `Database:SeedDemoData` to `false` in `appsettings.Development.json` to start without them. `scripts/sql/dev-reset-fresh-setup.sql` empties an existing development database again: it removes units, taxes, products, categories, expense categories, accounts and all documents, and keeps customers and suppliers unless `-v wipe=customers,suppliers` is passed.
- Solution can be opened via `backend/Sellora.slnx` in Visual Studio (17.13+), Rider, or `dotnet` CLI.
