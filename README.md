# KTC Monitoring Portal

Centralised NOC-style monitoring portal for ATM / device, ticket, incident, transaction, EJ and client information, built on the **KTC REST API** (ASP.NET Core 8 MVC, Clean Architecture, EF Core + SQL Server, Polly, Serilog, SignalR, Bootstrap 5, DataTables, Chart.js).

## Quick start (demo — no KTC server needed)

```bash
dotnet run --project src/Ktc.Web          # Development => in-memory DB + built-in mock KTC server
# open http://localhost:5080  — any username/password signs in (password "invalid" is rejected)
dotnet test                               # 52 unit tests
```

Development mode uses `Ktc:UseMock=true` (an in-process KTC stand-in producing KTC-shaped data) and `Database:Provider=InMemory`.
**Nothing in Production uses either.**

## Documentation
| Doc | Content |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Solution architecture, project structure, modules, **API reconciliation**, security, extension points |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Configuration, SQL Server, IIS / Linux / Docker deployment, go-live verification checklist |
| [docs/database/schema.sql](docs/database/schema.sql) | Idempotent SQL Server schema script (generated from the EF migration) |

## Layout
```
src/Ktc.Domain          entities + enums (no dependencies)
src/Ktc.Application     DTOs, interfaces, pure business logic (health, SLA, PAN masking, Postman importer, EJ timeline)
src/Ktc.Infrastructure  EF Core, repositories, KTC API client (Polly), token mgmt, services, background jobs, mock KTC
src/Ktc.Web             MVC controllers, Razor views, SignalR hub, middleware, JS/CSS
tests/Ktc.Tests         xUnit
```
