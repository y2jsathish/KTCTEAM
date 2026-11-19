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
| `Ktc__CountersEndpoint`, `Ktc__CounterThresholds__{name part}`, `Ktc__CounterLookbackDays` | ATM 360 **Application Counters** / **XFS Counters** tabs. XFS counters come from `CountersEndpoint`, default `api/v1/Client/{atmId}/Counters`, the route KTC's own UI uses (list of KTCCounter: component DBName such as `KXDevice.KXCardReader`, counter name such as `GoodReads`, value); devices it does not list fall back to counter-like properties in `Client/{id}/CurrentStatus`, and the note counts of `Client/{id}/CurrentCashUnitStatus` are added. KTC sends no thresholds: `CounterThresholds` flags a counter as Warning when its name contains the key (e.g. `bad reads` = 50, `jam` = 3). Application counters are counted from `TransactionData/Search` over `CounterLookbackDays` (default 7); "since reset" = since the last REPLENISH / ADD_CASH / CLR_CASH session. `/diag/ktc-token` shows the raw Counters answer and, per XFS device, which CurrentStatus properties are read as counters |
| `Ktc__TransactionCountersEndpoint`, `Ktc__TransactionNotesMax` | **Notes dispensed / Notes accepted** on the Application Counters tab. Each cash transaction since the reset is looked up in `TransactionCountersEndpoint` (default `api/v1/HistoricalCounter/ClientTransactionCounters?clientid={atmId}&transactionid={transactionId}`, what KTC's UI reads per transaction), at most `TransactionNotesMax` (default 300) per page load; answers are cached for 12 hours. Without per-transaction counters the totals come from the dispenser `Dispensed` and acceptor `Deposited` counters and Today shows n/a. The XFS tab's device checkboxes are kept in the browser; the export takes `devices=CDM,CIM,IPM`. |
| `Ktc__DenominationValues__{id}` | Note value of a KTC DenominationID (e.g. `Ktc__DenominationValues__28=100`) for the **Notes by denomination** table. Only needed for ids KTC's `Denomination` list and `Denomination/{id}` have no `CurrencyValue` for; the Application Counters warning names such ids. |
| `Ktc__EjDayFirst` | ATM 360 **EJ Counters** tab: the cash counter report the ATM prints to its EJ on restart (MACHINE NO, DATE-TIME, TYPEn-value columns of CASSETTE / +REJECTED / +DISPENSED / -DEPOSITED / =TOTAL, LAST CLEARED), read from `Client/{id}/EJRow` one day at a time. Dates are read as MM/dd/yy; set `true` for dd/MM/yy (a date in the future always falls back to the other order). |
| `Portal__AdminUsers__0`, `Portal__OperatorUsers__0` | usernames as typed at login; everyone else is Viewer |
| `Portal__TimeZoneId` | `Asia/Dubai` — defines "today" and hourly buckets |
| `Notifications__Smtp__*`, `Notifications__TeamsWebhookUrl`, `Notifications__DefaultRecipients__0`, `Notifications__PortalBaseUrl` | alert/report delivery |
| `DataProtection__KeysPath` | a persisted, access-restricted folder (required across restarts / multiple nodes) |

### Sign in with Microsoft (Entra ID / MSAL)

Optional. When enabled, the login page shows **Sign in with Microsoft** next to the KTC credential form. Roles still come from
`Portal:AdminUsers` / `Portal:OperatorUsers`, matched on the user's sign-in name (the `preferred_username` claim, normally the UPN/email,
for example `name@brinks.com`), and the portal still issues its own cookie session.

1. In Entra ID, register an app for the portal: *Web* platform, redirect URI `https://<portal host>/signin-oidc`, and create a client secret.
2. Set (never commit the secret; use environment variables, user-secrets or the machine-wide `appsettings.Production.json`):

| Setting | Note |
|---|---|
| `EntraId__Enabled` | `true` |
| `EntraId__TenantId` | directory (tenant) GUID or domain. `common` / `organizations` are not supported |
| `EntraId__ClientId` / `EntraId__ClientSecret` | from the app registration |
| `EntraId__UsernameClaim` | default `preferred_username` (falls back to `upn`, then `email`) |
| `EntraId__KtcLogin` | default `true`: also calls KTC `api/v1/Login` with `AuthMode` `MSAL`, the user name and the Entra ID token, as the KTC desktop client does, so KTC calls run as that user |
| `EntraId__FallbackToServiceAccount` | default `false`. If KTC does not accept the portal's token, `true` lets the user in and runs KTC calls with `Ktc:ServiceAccount` (actions are then attributed to the service account) |

KTC's own desktop client signs in with its app registration (visible in its trace as `MSALAuthParameters`, scopes `Profile`,
`UsernameClaim` `preferred_username`) and sends the **ID token** to KTC. The portal has a different client id, so KTC must accept
tokens issued to it (ask the KTC administrator), otherwise use the service-account fallback. The KTC session refresh uses the
same token, so it ends when the Entra ID token expires (about an hour) and the user is asked to sign in again.

### Operator and Admin users

Writing to KTC from the portal (adding a comment or changing a dispute or ticket, running ATM commands) needs the **Operator** or
**Admin** role. Everyone else is a **Viewer**, and the portal refuses their writes with a message naming this setting.

* **Where:** `src/Ktc.Web/appsettings.json` (or the `appsettings.Production.json` next to the app), section `Portal`, keys `OperatorUsers` and `AdminUsers`. Each is an array of sign-in names.
* **Value:** the name exactly as typed on the portal's login page (the KTC user name, such as the one KTC shows on comments). Case does not matter. Use the same form every time you sign in (`name@domain` or `DOMAIN\\name`), because the match is on that text.
* **Environment variables** (no file edit): `Portal__OperatorUsers__0`, `Portal__OperatorUsers__1`, … and `Portal__AdminUsers__0`, …
* **Restart:** yes, the app reads the setting at start-up. Then sign out and sign in again, because the role is stored in the sign-in cookie.

```json
"Portal": {
  "AdminUsers": [],
  "OperatorUsers": [ "Sathishkumar.Coumar@brinks.com" ]
}
```

```
Portal__OperatorUsers__0=Sathishkumar.Coumar@brinks.com
```

Without editing settings: an existing user's role can be raised once in the portal database (table `Users`, column `RoleId`, to the
`Roles` row named Operator); the portal keeps such a promotion. The user must have signed in once so the row exists. In demo mode
(`Ktc:UseMock` true) with `AdminUsers` empty, every user is an Admin. The portal has no screen for changing roles.

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
