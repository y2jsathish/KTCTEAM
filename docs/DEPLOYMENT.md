# Deployment Guide

## 1. Prerequisites
* .NET 8 runtime (ASP.NET Core Hosting Bundle for IIS) · SQL Server 2019+ · network path to the KTC REST API host (HTTPS)
* A KTC **service account** (read-only rights recommended) for background refresh, alerts and scheduled reports
* SMTP relay and/or a Teams "Workflows → webhook" URL for notifications

## 2. Configuration (never commit secrets)
Settings bind from `appsettings.json` → environment variables (`__` = section separator) → user-secrets.

| Setting | Example / note |
|---|---|
| `ConnectionStrings__KtcPortal` | `Server=sql01;Database=KtcMonitoringPortal;User Id=…;Password=…;TrustServerCertificate=True` |
| `Database__Provider` | `SqlServer` (default). `InMemory` is for development only |
| `Ktc__BaseUrl` | `https://ktc-restapi.corp.local/` (**required** — not in the Postman file) |
| `Ktc__UseMock` | must be `false` in production |
| `Ktc__AuthModes__0` | e.g. `DOMAIN` (login drop-down; `GET api/v1/AuthModes` lists what KTC supports) |
| `Ktc__ServiceAccount__Username/Password/AuthMode` | enables background refresh + alerting + scheduled reports |
| `Ktc__SeverityMap__{level}` / `Ktc__TicketStatusMap__{statusByte}` | map KTC levels / status bytes → P1–P4 / Open·Assigned·InProgress·Resolved·Closed |
| `Ktc__TicketSearchGroupId` / `TransactionSearchGroupId` | `GroupID` used in the search bodies (sample values 2 / 1) |
| `Portal__AdminUsers__0`, `Portal__OperatorUsers__0` | usernames as typed at login; everyone else is Viewer |
| `Portal__TimeZoneId` | `Asia/Dubai` — defines "today" and hourly buckets |
| `Notifications__Smtp__*`, `Notifications__TeamsWebhookUrl`, `Notifications__DefaultRecipients__0`, `Notifications__PortalBaseUrl` | alert/report delivery |
| `DataProtection__KeysPath` | a persisted, access-restricted folder (required across restarts / multiple nodes) |

## 3. Database
The app applies EF migrations at start-up. To have a DBA apply it instead: run `docs/database/schema.sql` (idempotent), then start the app (migration is then a no-op). Grant the app login `db_datareader`, `db_datawriter`, and `db_ddladmin` only if it should self-migrate.
Regenerate after model changes:
```bash
dotnet tool install --global dotnet-ef --version 8.0.11
dotnet ef migrations add <Name> -p src/Ktc.Infrastructure -s src/Ktc.Infrastructure -o Data/Migrations
dotnet ef migrations script --idempotent -p src/Ktc.Infrastructure -s src/Ktc.Infrastructure -o docs/database/schema.sql
```
Retention: schedule a SQL Agent job to purge `AuditLogs`/`AlertHistories` per policy.

## 4. Publish & host

```bash
dotnet publish src/Ktc.Web -c Release -o ./publish
```
**IIS (Windows):** install the Hosting Bundle, create an app pool with *No Managed Code*, point the site at `publish`, bind an HTTPS certificate, set env vars on the pool (or `web.config` `environmentVariables`), enable WebSockets (SignalR). Run the pool identity with rights to the Data Protection key folder and `logs/`.

**Linux (systemd + nginx):**
```ini
[Service]
WorkingDirectory=/opt/ktc-portal
ExecStart=/usr/bin/dotnet /opt/ktc-portal/Ktc.Web.dll
Environment=ASPNETCORE_ENVIRONMENT=Production
EnvironmentFile=/etc/ktc-portal.env     # holds the secrets, chmod 600
Restart=always
```
nginx must forward `Upgrade`/`Connection` headers for `/hubs/portal` and set `X-Forwarded-Proto`; add `app.UseForwardedHeaders()` if terminating TLS at the proxy.

**Docker (example):**
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY publish/ .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet","Ktc.Web.dll"]
```
Mount a volume for `DataProtection__KeysPath` and `logs/`.

**Scale-out:** more than one node requires (a) shared Data Protection keys, (b) a distributed session/cache (Redis/SQL — replace `AddDistributedMemoryCache`), (c) the SignalR Redis backplane, (d) running background jobs on a single node (`Portal__EnableBackgroundJobs=false` elsewhere).

## 5. Go-live verification checklist (do this against the real KTC)
1. `Ktc__UseMock=false`, BaseUrl set; sign in — confirm the login response shape is parsed (token & expiry; otherwise adjust `KtcAuthService.ParseToken`).
2. **Verify payload field names** with *API Catalog → Execute* on `Client?includedeleted=false`, `LastClientContact`, `ActiveErrorExtra?abovelevel=0`, `HistoricalError`, `Ticket/Search`, `TransactionData/Search`, `Client/{id}/EJRow`, `Client/{id}/OverallAvailability`, `Business`, `Region`, `Branch`. Compare with the alias lists in `KtcDataService` / `KtcJson`; extend aliases where needed. Dashboard header shows "Partial data" warnings when calls fail.
3. Confirm `Ktc:SeverityMap` (which KTC level is P1?), `TicketStatusMap` (status bytes), `GroupID`s, EJ `Value3` field names (`Ktc__EjFields__0`), and the time-index semantics of `OverallAvailability`.
4. Confirm KTC rejects expired tokens with 401 (the client refreshes once and retries).
5. Send a test notification (Alert Rules → *Send test*) and verify SMTP/Teams.
6. Open every page in the target browsers (light & dark) — UI was not browser-rendered during development.
7. Review the API Catalog: only intended endpoints enabled; confirm sensitive endpoints show "blocked".
8. Check `/health`, `logs/`, API Health page (all green), Audit Log entries.

## 6. Operations
* Logs: Serilog console + rolling files (`logs/`), optional SQL sink (`Serilog:SqlServerSink=true`). Every request carries `X-Correlation-ID`.
* Monitoring: `GET /health` (DB check), API Health page, alert rules on API success/latency.
* Backups: standard SQL Server backups; the portal holds no KTC business data beyond caches.
