# KTC Monitoring Portal — Solution Architecture

## 1. Context

```
 Browser (Bootstrap5/jQuery/DataTables/Chart.js/SignalR)
        │  HTTPS + cookie (+ SignalR websocket)
        ▼
 ┌──────────────────────────── Ktc.Web (ASP.NET Core 8 MVC) ───────────────────────────┐
 │ Controllers/Views · DataController (JSON) · PortalHub · Middleware (security headers,│
 │ correlation id, API error) · Cookie auth + server-side Session · Rate limiter        │
 └───────────────┬──────────────────────────────────────────────────────────────────────┘
                 ▼
 ┌──────── Ktc.Infrastructure ────────┐        ┌──── Ktc.Application (pure) ────┐
 │ KtcApiClient ─ Polly ─ ApiHealth   │        │ DTOs · interfaces · options    │
 │ KtcTokenProvider / KtcAuthService  │◄──────►│ AtmHealthCalculator, SLA, PAN  │
 │ KtcDataService (payload→DTO)       │        │ PostmanImporter, ApiCatalogRules│
 │ Dashboard/Alert/Report/Export/Audit│        │ EjTimeline, FailureClassifier  │
 │ EF Core + repositories + seeder    │        └────────────────────────────────┘
 │ Background jobs · Mock KTC (dev)   │                    ▲
 └──────┬─────────────────┬───────────┘                    │ depends on
        ▼                 ▼                          Ktc.Domain (entities/enums)
   KTC REST API      SQL Server            SMTP · Teams webhook
```
Dependencies point inward (Web → Infrastructure → Application → Domain). The Application layer has no EF/HTTP/ASP.NET dependency, so the business rules are unit-tested in isolation.

## 2. API reconciliation — what the KTC collection actually contains

The brief's mapping was checked against the supplied Postman collection (805 requests) and the KAL API documentation. **Several mapped routes do not exist**; the portal maps the *business concept* to the closest real KTC resource:

| Brief | In the KTC API | Portal implementation |
|---|---|---|
| `Device` (`GET /Device`, `/Device/{id}`, `POST /Device/Search`) | **No Device controller.** ATMs are `Client` records | `GET Client?includedeleted=`, `Client/{id}`, `LastClientContact`, `Client/{id}/OverallAvailability`, `Client/{id}/Top5UnavailableReasons`, `Business/Branch/Region` for ownership & location |
| `Incident` (`GET /Incident`, `POST /Incident/Search`) | **No Incident controller.** Faults are error codes | `ActiveErrorExtra?abovelevel=0` (open), `HistoricalError`, `Client/{id}/HistoricalError` (history/RCA); level → P1–P4 via `Ktc:SeverityMap` |
| `Client` = bank (`/Client/{id}/Contacts`, `/Actions`) | `Client` = **ATM**; banks are `Business`. `/Contacts` and `/Client/{id}/Actions` do not exist | Portal "Client" = KTC `Business` (`Business`, `Branch`, `Region`) |
| `GET /Ticket`, `GET /Ticket/{id}`, `POST /Ticket/Search`, `PUT /Ticket/{id}` | Exists (`GET Ticket?errorID=`, `Ticket/{id}`, `Ticket/Search`, `Ticket/{id}/SLA`) — there is no bare `GET /Ticket` | `Ticket/Search`, `Ticket/{id}`, `Ticket/{id}/SLA`. **Ticket writes are not performed** (see §6) |
| `POST /TransactionData/Search`, `GET /TransactionData/{id}` | Exists | Used as specified |
| `POST /Client/{id}/EJRow`, `/EJPlusTime` | Exists (body `{Value1: start, Value2: end, Value3: [field names]}`) | `EJRow` |
| `Login`, `ReLogin`, `Logoff` | Exists (`Relogin` capitalised differently in the doc) | Used as specified |

The Postman collection has **no base URL, no response samples and empty `url.raw`** (routes live in the request *name*). Consequences, all handled:
* base URL is configuration (`Ktc:BaseUrl`);
* the importer derives routes from names;
* response parsing is tolerant (`KtcJson`: case-insensitive alias lists). **Field names are assumptions until verified against live payloads** — see the verification list in DEPLOYMENT.md. Mapping lives in one place (`KtcDataService`) and the API Catalog learns real response schemas on first execution (`ApiEndpoints.ResponseSchema`).

## 3. Modules → implementation

| # | Module | Where |
|---|---|---|
| 1 | Authentication | `AccountController`, `KtcAuthService`, `KtcTokenProvider`. Token + expiry in server-side session; auto-refresh via `ReLogin` (credentials kept **encrypted with Data Protection**, server-side only); header shows user, status, token countdown |
| 2 | Executive dashboard | `Home/Index`, `DashboardService.BuildAsync`; 9 KPI cards + 7 charts; 60 s refresh + SignalR push |
| 3 | ATM monitoring | `Atm/Index`; filters client/city/region/status; green/amber/red health via `AtmHealthCalculator` |
| 4 | Incidents | `Incidents/Index`; P1–P4 tiles, drill-down panel, assign / comment / escalate (`IncidentActions`) |
| 5 | Tickets | `Tickets/Index`; search by ATM/ticket/client/date; status tiles; SLA achieved/breached/near-breach; banner for breach < 1 h |
| 6 | Transactions | `Transactions/Index`; stats, filters, failure grouping (host / communication / cash retract / dispense / other) |
| 7 | EJ search | `Ej/Index`; per-ATM search by card, sequence, text, date; transaction timeline; **PAN masked**; Excel/CSV/PDF |
| 8 | Alert centre | `AlertEngine` (9 rule types), `AlertRules`/`AlertHistory`, e-mail (MailKit) + Teams webhook + portal/SignalR; cooldown de-duplication |
| 9 | Reporting | `ReportService` (6 reports), PDF (QuestPDF) / Excel (ClosedXML) / CSV; `ReportSchedules` daily/weekly/monthly e-mailed by `ReportSchedulerJob` |
| 10 | API layer | `KtcApiClient` + Polly (retry w/ backoff+jitter for idempotent calls, circuit breaker, per-attempt timeout) + `ApiHealthHandler` + Serilog |
| 11 | Audit | `AuditService` + `[Audit]` filter: logins, searches, ticket/incident/ATM views, reports, admin changes (PAN-masked) |
| – | ATM 360 | `Atm/Detail` → `DashboardService.GetAtmDetailAsync` (device, incidents, tickets, transactions, EJ + timeline, availability, health score) |
| – | Management / Clients / RCA / Global search | `Home/Management`, `Home/Clients`, `Home/Rca` (recurring ≥ 3 occurrences), header search (`/data/search`) |

## 4. API Mapping Framework

Tables `ApiModules → ApiEndpoints → ApiMappings → ApiFieldMappings`.

* **Auto-discovery** (`PostmanImporter` + `ApiCatalogRules` + `ApiRegistryService`), run at first start (seed file) and via *Import Postman*: detects authentication / monitoring / search / update endpoints, groups modules (123 from the supplied collection, curated business names for the KTC modules in the brief), generates navigation (`ApiNav` view component → "API Explorer"), default widgets (`ApiMappings`), and the catalog. Re-import is idempotent and preserves admin enable/disable and mappings.
* **Safety defaults** (deliberate deviation from "no manual configuration"): only **read-only** endpoints (393 GET + 21 search/read-POST of 801) are enabled automatically. **358 mutating endpoints and 16 auth endpoints import disabled**; **credential/key endpoints** (`AdminPassword`, `BitLockerPassword`, `LUKSPassword`, `OfflineSupervisorPassword`, `Atm3DesKey`, certificates, …) are flagged sensitive and can never be enabled or executed. Mutating execution additionally requires the Admin role.
* **Execution framework** (`ApiExecutionService`): Execute (path/query parameter substitution, escaping), Cache (TTL = mapping refresh interval), Retry/timeout/breaker (HTTP pipeline), Export (Excel/CSV/PDF of any JSON array), Schedule (`ApiMappingSchedulerJob` for Scheduled/Realtime mappings without parameters; results cached in `DashboardCaches`). Modes: Realtime / Scheduled / Manual.
* **Field → widget mapping**: admins add `ApiFieldMappings` (JSON path + label + widget + aggregation) per mapping; generated screens (`/ApiAdmin/Screen/{name}`) render them as KPI cards/counters, otherwise as a table.
* **API health**: per-route rolling window (100 calls): response time, failures, success rate, timeouts, consecutive failures. Green < 2 s, yellow 2–5 s, red > 5 s (or > 3 consecutive failures). Alert rules: success < 95 %, response > 5 s, consecutive failures > 3.

## 5. Health logic (`AtmHealthCalculator`)

* **Healthy**: no active incidents and last communication < 10 min.
* **Warning**: open P3/P4, or last communication > 10 min (or never).
* **Critical**: open P1/P2, device offline (no contact > `Ktc:OfflineMinutes`, default 60, or a communication-failure fault), or host disconnect.
Score 0–100 is derived from the same inputs (used in ATM 360 and the grid).

## 6. Security design

* **AuthN**: KTC credentials validated by KTC itself; portal cookie (`HttpOnly`, `SameSite=Lax`) + server-side session. If the server-side session disappears (restart) the cookie is rejected. Login rate-limited (8/min/IP).
* **AuthZ**: roles Admin / Operator / Viewer (`Roles`, `Users`); elevated roles come from configuration (`Portal:AdminUsers`, `Portal:OperatorUsers`) so the DB cannot be silently self-promoted. Fallback policy requires authentication everywhere.
* **CSRF**: global `AutoValidateAntiforgeryToken`; the JS wrapper sends the token header.
* **Secrets**: service-account/SMTP credentials via environment variables / user-secrets / vault — never in the repository. Session-held user credentials are Data-Protection encrypted.
* **PAN**: masked (first 6/last 4) at the data-service boundary — raw journal text never reaches controllers/JSON/exports/logs; card search is matched server-side on raw text; audit stores masked values; global search rejects PAN-like input. Excel/CSV cells are neutralised against formula injection.
* **Headers**: CSP, X-Frame-Options, nosniff, Referrer-Policy.
* **Data scoping**: with a service account the dashboard is one shared snapshot (an ops view); without one, snapshots/caches are per-user so KTC permissions are respected.
* **No ticket write-back**: assignment/comment/escalation are portal-side (`IncidentActions`) because `PUT Ticket/{id}` requires the whole ticket payload and writes to a production system; add it behind an explicit decision.

## 7. Real-time & background work

`DashboardRefreshJob` (every 60 s, needs `Ktc:ServiceAccount` or mock): rebuild snapshot → persist to `DashboardCaches` → SignalR `dashboardUpdated` (lightweight summary) → evaluate alert rules → push API health. Pages re-fetch on that event and also poll (60 s / 30 s) as a fallback. `ReportSchedulerJob` (1 min) and `ApiMappingSchedulerJob` (30 s) run similarly.
Per-ATM availability needs one KTC call per ATM; it is cached (`AvailabilityCacheMinutes`) and warmed progressively (`AvailabilityWarmupPerCycle`) so a 1,000-ATM estate does not hammer KTC; ATMs not yet fetched show "n/a" (never a fabricated value).

## 8. Database

14 tables (+ EF history) — `Users, Roles, AuditLogs, DashboardCaches, SavedSearches, FavoriteAtms, AlertRules, AlertHistories, ReportSchedules, ApiModules, ApiEndpoints, ApiMappings, ApiFieldMappings` and `IncidentActions`. See `docs/database/schema.sql`. `SavedSearches` is modelled but has no UI yet.

## 9. Known gaps / honest limitations
* **Verified only against the built-in mock** — there is no access to a live KTC from the build environment. Field-name aliases, ticket status semantics, severity levels, login response shape, `HistoricalError` / `OverallAvailability` query parameters are assumptions (checklist in DEPLOYMENT.md).
* UI was not rendered in a browser during development (CDNs blocked in the build sandbox); server-rendered pages, all JSON endpoints and JS syntax were tested. Do a visual pass on first deploy.
* Alerting/background refresh requires a KTC service account; otherwise dashboards refresh per user and alerts do not fire.
* Single-node session/cache by default (in-memory). Use Redis/SQL distributed cache + shared Data Protection keys to scale out.
* CDN-hosted front-end libraries; vendor them locally for air-gapped networks.
* `SavedSearches` UI, user-management UI, and ticket write-back are not implemented.
