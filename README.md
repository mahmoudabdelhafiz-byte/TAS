# TAS — Truck Appointment System

Truck appointment and landside management for **container and multipurpose terminals** — the first module of **Yardsyncs**.
A generic product: one deployment serves many terminals and operators, hosted by us or installed on-premise; each terminal's choices (TOS, gate identification, driver identity, fees…) are settings, not code.
Next module: **Yardsyncs Autogate**, built on the same gate API.

## Scope

- **Capacity management** — default weekly templates per hour and transaction category (import, export, empty, breakbulk…), seasonal templates, and exceptional overrides (set / adjust / close) for specific days and hours.
- **Booking** — single, bulk and dual-transaction appointments, validated against the TOS (availability, holds, customs release, payment, booking/EDO, VGM, cut-off) before the truck moves.
- **Multipurpose cargo** — bookings by BL / cargo reference in trips or tonnage, drawn down against the cargo allocation.
- **Gate** — Autogate / OCR plate matching, digital gate pass (rotating QR), early/late grace windows, trouble desk.
- **Driver app** — simple multilingual app (Arabic, English, Urdu, Hindi): traffic-light status with reason, geofence check-in and ETA, yard guidance, chat with the control room.
- **Trucking companies** — web portal and API for dispatchers.
- **Operations** — capacity configuration, live arrivals board, KPIs (slot utilization, no-shows, turn time).

## Stack

| Part | Technology |
|---|---|
| API & background jobs | ASP.NET Core |
| Web portal (terminal + trucking companies) | React |
| Driver app | Flutter (Android / iOS) |
| Database | PostgreSQL + PostGIS (via EF Core; SQL Server provider possible for on-premise) |

## Repository layout

```
docs/                         design documents (docs/data-model.md) and progress log (docs/progress.md)
db/schema.sql                 PostgreSQL + PostGIS schema
src/Tas.Domain                entities
src/Tas.Application           capacity engine (pure logic: planning, availability, reconciliation)
src/Tas.Infrastructure        EF Core + Npgsql, slot generation, availability queries, booking counters
src/Tas.Api                   ASP.NET Core minimal API
tests/Tas.Application.Tests   unit tests for the capacity rules
tests/Tas.Infrastructure.Tests integration tests against PostgreSQL (run when TAS_TEST_DB is set)
tests/Tas.Api.Tests           end-to-end API tests incl. auth (run when TAS_API_TEST_DB is set)
db/migrations/                scripts to upgrade an existing database
integrations/n4/              Navis N4: Groovy code extension (TasLookup) and setup guide
web/                          web portal (React + TypeScript, Vite) for trucking companies and terminal staff
mobile/                       driver app (Flutter)
```

## Deploying

`Dockerfile` builds one image with the API and the portal. `deploy/` runs it on one server (on-premise at a terminal
or a cloud VM) with PostgreSQL + PostGIS, HTTPS (Caddy) and nightly backups; the same image runs on cloud container
platforms with a managed database. Step by step, upgrades, restore and monitoring: [`deploy/README.md`](deploy/README.md).

- The database is created and upgraded by the application itself from `db/` (see [`db/README.md`](db/README.md));
  `dotnet Tas.Api.dll migrate` does it on demand.
- Health: `GET /health/live`, `GET /health/ready` (database reachable and up to date); `GET /api/ops/status` for
  admins (version, database, message and event backlogs).
- Production settings: `Logging__Json=true` (one JSON object per line), `Proxy__TrustForwardedHeaders=true` behind a
  reverse proxy, HSTS and browser security headers (CSP, no framing) are on outside Development.
- Security and personal data: [`docs/security.md`](docs/security.md) — review findings, what each customer decides
  (data location, providers, privacy notice, breach reporting). Outside Development / Testing the app refuses to start
  with test switches on, builds password links only from `PasswordLinks:PortalUrl`, and keeps codes and links out of
  the logs; `/api/ops/status` lists `warnings` for settings to look at.
- Versions, images (`ghcr.io/mahmoudabdelhafiz-byte/yardsyncs`), environments, releases, upgrade and rollback:
  [`docs/releasing.md`](docs/releasing.md).
- Several terminals on one installation: per terminal code, its own TOS (`Terminals__<code>__Tos`), the name messages
  are signed with (`Terminals__<code>__Brand`) and any message setting (`Terminals__<code>__Notifications__…`:
  providers, accounts, SMS sender name, email sender); the installation's settings fill the rest. `/api/ops/status`
  lists each terminal's set-up.
- Branding: name, main colour, logo and (for operators) own web address for the installation and each operator
  (`/api/branding`, `/api/operators/{id}/branding`); shown at sign-in, in the portal, the drivers' app and messages.
- Language packs: a JSON file per language in `deploy/languages/` (from `template.json`) adds a portal, app or
  message language, or corrects a built-in one, without a new version; `GET /api/languages`. After changing portal or
  app texts run `npm run language-template` in `web/` (a test checks the template is current).
- Country packs (`Country`, default SA; also AE, OM, BH, KW, QA): local mobile forms, plate letters, the drivers' ID
  document, currency and public holidays; a terminal can have its own (`Terminals__<code>__Country`) for holidays and
  currency. `GET /api/country`, `GET /api/terminals/{id}/holidays?year=`.
- Operators: a system admin groups the terminals one company runs (`/api/operators`,
  `PUT /api/terminals/{id}/operator`); the operator's admins (role `OperatorAdmin`) manage all of them. Competing
  companies get separate installations.
- Load test of the peak booking hour: [`loadtest/`](loadtest/README.md) (k6; runs in CI on every pull request).
- Personal data is cleaned up daily (`Privacy:LocationDays` 30, `Privacy:MessageDays` 180, `Privacy:EventDays` 90,
  `Privacy:TokenDays` 30). Drivers: `GET /api/companies/{c}/drivers/{d}/personal-data` (copy) and
  `POST …/erase` with a reason (company admin or terminal admin; refused while a visit is under way).

## Running locally

Requires the .NET 10 SDK and PostgreSQL 15+ with PostGIS.

```bash
createdb tas
psql -d tas -f db/schema.sql

# set ConnectionStrings:Tas in src/Tas.Api/appsettings.json (or an environment variable)
dotnet run --project src/Tas.Api
```

Production settings (environment variables):

| Setting | Purpose |
|---|---|
| `ConnectionStrings__Tas` | PostgreSQL connection string |
| `Jwt__SigningKey` | **Required.** Random secret, at least 32 characters. The API refuses to start without it |
| `Bootstrap__AdminEmail`, `Bootstrap__AdminPassword` | Creates the first SysAdmin on start if none exists; remove afterwards |
| `DriverLogin__SessionDays`, `DriverLogin__CodeMinutes` | Driver app session length (default 30 days, ends earlier when the driver signs in on another phone) and sign-in code lifetime (5 min) |

Upgrading an existing database: run the scripts in `db/migrations/` in order.

Tests:

```bash
dotnet test tests/Tas.Application.Tests

# integration and API tests each need their own database created from db/schema.sql
export TAS_TEST_DB="Host=localhost;Database=tas_test;Username=postgres;Password=postgres"
export TAS_API_TEST_DB="Host=localhost;Database=tas_api_test;Username=postgres;Password=postgres"
dotnet test tests/Tas.Infrastructure.Tests
dotnet test tests/Tas.Api.Tests
```

CI (GitHub Actions) builds the solution and runs both test projects against a PostGIS container on every push.

### Web portal

Requires Node.js 24 (LTS).

```bash
cd web
npm install
npm run dev        # http://localhost:5173, forwards /api to the API on http://localhost:5000 (TAS_API_URL to change)
npm test           # unit tests
npm run build      # type-check, then build into src/Tas.Api/wwwroot — the API then serves the portal at /
```

Screens by role:

| Role | Screens |
|---|---|
| Company admin / dispatcher | Book a truck (one, or several by BL / container list), Track containers (status, follow, book the ready ones), Bulk upload, Appointments (own company), Fleet & users |
| Planner, terminal admin | Slot board, Control room, KPIs, Capacity setup, Terminal map (terminal admins edit, planners view), Appointments (all companies), Book a truck / Bulk upload (on behalf of a company), Gate, Fleet & users (companies for terminal admins) |
| Gate clerk | Gate (board, truck check, gate in/out, trouble desk), Control room, Appointments |

The terminal map editor draws blocks, gates, exits and roads (one-way in the drawn direction) over a background map, imports a GeoJSON export from CAD / GIS, checks that every block can be reached from the gate and left to the exit, and tries routes before saving. The background tiles come from the `yardsyncs-map-tiles` meta tag in `web/index.html` (OpenStreetMap by default; set any `{z}/{x}/{y}` tile server, or leave it empty for a plain grid on sites without internet) with its credit line in `yardsyncs-map-attribution`.

Arabic and English, right-to-left in Arabic. Times are shown in the terminal's time zone. Messages that come from the server (check results, errors) are in English for now.

**Documentation** (`#/docs`) is public — no sign-in — on every installation: all user guides, and for IT teams and
integrators the system overview, API, integration events, operations, security and personal data, and the AI
assistant (English). The sign-in page links to it.

**AI assistant**: a chat panel on every page, in Arabic or English, when `Assistant__ApiKey` (Anthropic) is set. It
answers from the guides and, unless `Assistant__LiveData=false`, from read-only look-ups made with the user's own
permissions (an appointment, the day's appointments, free hours, container status, and for terminal staff the
terminal right now). It never changes anything. No conversation text is stored; questions and tokens per user and day
are (daily limit `Assistant__DailyQuestionsPerUser`, 60). `POST /api/terminals/{id}/assistant`, `GET /api/assistant`.

**Help** (`#/help`) has the user guides for each role in Arabic and English, ready to print. They are Markdown in
`web/src/help/en` and `web/src/help/ar`; when a screen's wording changes, the web tests point at the guides that
name it.

## API (so far)

All endpoints except `/health` and login/refresh/logout need `Authorization: Bearer <accessToken>`.

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/auth/login` | Email + password → access token (30 min), refresh token (14 days), profile |
| `POST` | `/api/auth/forgot-password` | `{email}` → a link to set a new password by email (60 min, works once); same answer for unknown emails; 3 per account per hour |
| `GET` / `POST` | `/api/auth/password-link` | `?token=` who the link is for; `{token, newPassword}` sets the password, ends all sessions, lifts a lock-out |
| `POST` | `/api/auth/refresh` | New token pair; the old refresh token stops working |
| `POST` | `/api/auth/logout` | Revoke a refresh token |
| `GET` | `/api/auth/me` | Signed-in user's profile |
| `POST` | `/api/auth/change-password` | Change password; signs out all other sessions |
| `POST` | `/api/users` | Create a user (within the admin's scope) |
| `POST` | `/api/terminals/{id}/slots/generate?from=2026-10-20&to=2026-10-27` | Regenerate slots after changing templates or overrides (local dates) |
| `GET` | `/api/terminals/{id}/availability?date=2026-10-20&category=IMP_PICKUP&companyId=1` | Slots of a day with remaining capacity for a category |
| `POST` | `/api/terminals/{id}/appointments` | Book a truck visit (1–4 moves; 2+ = dual transaction) |
| `POST` | `/api/terminals/{id}/containers/lookup` | Book by BL or several containers: `{category, blNo}` or `{category, containers[], bookingRef?}` → each container with size and status (Ready / Warning / Blocked / Booked) and what stops it. Books nothing |
| `POST` | `/api/terminals/{id}/containers/track` | `{blNo}` or `{containers}`: where each container stands — `READY` to collect, `NOT_AVAILABLE`, `ON_HOLD` (with the reason), `BOOKED` (own appointment shown; others only "booked"), `COLLECTED` — and each check (in yard, customs, line release, terminal hold, charges). Books nothing |
| `GET` / `POST` / `DELETE` | `/api/terminals/{id}/containers/followed[/{no}]` | Containers the user follows: re-checked every 15 min (`Tracking:IntervalMinutes`), email (`CONTAINER_READY`) the moment one becomes ready; 30 days or until collected; 200 per user |
| `GET` | `/api/terminals/{id}/appointments/{no}` | One appointment with checks and live traffic light |
| `GET` | `/api/terminals/{id}/appointments?date=2026-10-20&companyId=1` | Appointments of a day |
| `POST` | `/api/terminals/{id}/appointments/{no}/cancel` | Cancel (until the cancellation cut-off; terminal staff any time) |
| `POST` | `/api/terminals/{id}/appointments/{no}/reschedule` | Move to another slot (allowed until gate-in, e.g. for late trucks) |
| `PUT` | `/api/terminals/{id}/appointments/{no}/assignment` | Assign / change truck and driver |
| `POST` | `/api/terminals/{id}/appointments/{no}/revalidate` | Re-check the appointment against the TOS now and refresh the traffic light |
| `GET` | `/api/terminals/{id}/appointments/{no}/notifications` | Messages queued / sent for the appointment (status, recipient, errors) |
| `POST` | `/api/terminals/{id}/appointments/{no}/start-trip` | Truck is leaving: re-check now; refused (422) while anything is red, else `EN_ROUTE` |
| `POST` | `/api/terminals/{id}/appointments/bulk?dryRun=true` | Bulk booking from an uploaded `.xlsx` / `.csv` (form field `file`) |
| `GET` | `/api/terminals/{id}/appointments/bulk/template?format=xlsx` | Download the bulk template (`xlsx` or `csv`) with instructions |
| `GET` | `/health` | Health check |

**Capacity configuration** (view: terminal staff of the terminal · change: Planner / TerminalAdmin):

| Method | Path | Purpose |
|---|---|---|
| `GET` / `POST` | `/api/terminals/{id}/capacity/templates` | List / create a template (default or seasonal with dates) |
| `PUT` | `/api/terminals/{id}/capacity/templates/{templateId}` | Replace a template's windows, quotas, dates, priority |
| `POST` | `/api/terminals/{id}/capacity/templates/{templateId}/deactivate` | Remove a seasonal template |
| `GET` / `POST` | `/api/terminals/{id}/capacity/overrides` | List / add an override (`Set`, `Adjust`, `Close`) — reason required |
| `POST` | `/api/terminals/{id}/capacity/overrides/{overrideId}/deactivate` | Lift an override |
| `GET` / `POST` | `/api/terminals/{id}/capacity/reservations` | Capacity held for a company or walk-ins |
| `POST` | `/api/terminals/{id}/capacity/reservations/{reservationId}/deactivate` | End a reservation |
| `GET` / `PUT` | `/api/terminals/{id}/capacity/release-rules` | Unused quota opens to all categories N hours before the slot |
| `GET` | `/api/terminals/{id}/capacity/slot-board?date=2026-10-20` | Capacity vs booked per slot and category, closed / over-capacity flags |
| `GET` / `PUT` | `/api/terminals/{id}/settings` | Grace windows, cancellation cut-off, per-truck daily limit |
| `GET` | `/api/terminals/{id}/audit?entity=capacity_override` | Who changed what, when, why — with before / after values |

Changes that affect capacity regenerate the affected slots immediately (the response says which). Bookings are never cancelled by a change; slots booked above their new capacity are flagged `overCapacity` for the planner.

Example — close tomorrow 10:00–11:00 for crane maintenance:

```json
POST /api/terminals/1/capacity/overrides
{ "startAt": "2026-10-20T10:00:00+03:00", "endAt": "2026-10-20T11:00:00+03:00", "action": "Close", "reason": "Crane 3 maintenance" }
```

Example — a weekly template window (times are local; `24:00` = midnight):

```json
{ "day": "Sunday", "start": "06:00", "end": "24:00", "totalCapacity": 30, "overbookPct": 5,
  "quotas": { "IMP_PICKUP": 15, "EXP_DROP": 12 } }
```

**Gate** (any terminal staff of the terminal; connect an Autogate / OCR system as a `GateClerk` account):

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/terminals/{id}/gate/arrivals` | Truck at an entry lane: `{ laneCode, plateRead?, containerReads?, qrToken?, appointmentNo? }` → **Admit** or **Refer** to the trouble desk |
| `POST` | `/api/terminals/{id}/gate/in` | Barrier opened for an arrived truck → `GATE_IN` |
| `POST` | `/api/terminals/{id}/gate/out` | Truck leaves (by appointment no. or plate) → `COMPLETED`, returns turn time |
| `GET` | `/api/terminals/{id}/gate/troubles?openOnly=true` | Trouble desk queue with the reason already shown |
| `POST` | `/api/terminals/{id}/gate/troubles/{troubleId}/resolve` | `Admit` (note) · `Reject` (note; capacity released) · `WalkIn` (book into the current slot) · `Dismiss` |
| `GET` | `/api/terminals/{id}/gate/board` | Trucks at the gate / inside with minutes, open troubles, completed today, average turn time |
| `GET` / `POST` | `/api/terminals/{id}/gate/lanes` | Lanes (create: TerminalAdmin) |

How an arrival is handled:
1. **Match** — QR code from the driver app or a typed appointment number win; otherwise the plate read (Arabic or Latin) picks the truck's appointment nearest to now; otherwise a container read. Reads that don't fit the appointment are shown as warnings.
2. **Re-check** against the TOS right there (holds can appear minutes before arrival).
3. **Decide** — admit if live, inside its window (slot ± grace), nothing red and truck/driver not blocked. Otherwise refer: `EARLY`, `LATE`, `NOT_CLEARED`, `BLOCKED`, `ALREADY_IN`, `CANCELLED`, `NO_APPOINTMENT` — with local times in the message (e.g. "Early: the arrival window opens at 09:30").
4. Every read, match, gate-in/out and trouble is written to the gate event log; arrival → gate-in → gate-out times give the turn time.

**Master data:**

| Method | Path | Who | Purpose |
|---|---|---|---|
| `GET` | `/api/terminals` | all signed-in users | Terminals the user can work at (companies: all active; staff: their own) |
| `GET` / `POST` | `/api/companies` | all (companies see only themselves) / TerminalAdmin | List & search / register a company |
| `GET` / `PUT` | `/api/companies/{id}` | own company or staff / TerminalAdmin | View / edit |
| `POST` | `/api/companies/{id}/status` | TerminalAdmin | Activate / suspend (reason required); suspended companies cannot book |
| `GET` / `POST` | `/api/companies/{id}/trucks` | own company or staff / CompanyAdmin, TerminalAdmin | Fleet; plates normalized (`أ ب ح ١٢٣٤` = `1234 ABJ` = `1234ABJ`) and unique |
| `PUT` | `/api/companies/{id}/trucks/{truckId}` | CompanyAdmin, TerminalAdmin | Edit plate |
| `POST` | `/api/companies/{id}/trucks/{truckId}/block` | TerminalAdmin | Block / unblock (reason required) |
| `GET` / `POST` / `PUT` | `/api/companies/{id}/drivers[/{driverId}]` | as trucks | Drivers; mobiles normalized to E.164 with the installation's country (`+966…` in Saudi Arabia) |
| `POST` | `/api/companies/{id}/drivers/{driverId}/block` | TerminalAdmin | Block / unblock |
| `GET` / `POST` / `PUT` | `/api/terminals/{id}/categories[/{categoryId}]` | everyone at the terminal / TerminalAdmin | Transaction categories; new ones are bookable at once |
| `GET` / `POST` / `PUT` | `/api/terminals/{id}/cargo[/{cargoId}]` | companies see own / Planner, TerminalAdmin | Multipurpose cargo releases (BL / DO) with trip and tonnage limits |
| `GET` | `/api/users` | CompanyAdmin, TerminalAdmin, SysAdmin | Users within your scope |
| `POST` | `/api/users/{id}/password-link` | as above | Email the user a link: invitation (no password yet, 7 days) or reset |
| `PUT` | `/api/users/{id}` | as above | Name, mobile, language, role (not your own role) |
| `POST` | `/api/users/{id}/active` | as above | Deactivate (reason required) / reactivate |
| `POST` | `/api/users/{id}/reset-password` | as above | Set a new password; ends the user's sessions |

Every master data change is written to the audit log. Tokens are re-checked against the account on every request, so deactivating a user or changing their role takes effect immediately.

**Re-checks before arrival:** a container that was clear at booking can get a customs hold, a withdrawn release or unpaid charges later. Booked appointments starting within 24 h are re-checked against the TOS (at most every 60 min each), on request, and when the trip starts. If an item turns red or clears, the change is written to the appointment history and a notification (`STATUS_RED` / `STATUS_CLEARED`) is queued for the driver (SMS) and the person who booked (email) in the `notification_log` outbox.

Background workers: slot generation (every 6 h), no-show marking (every 10 min), re-checks (every 15 min), notifications (every 30 s).
Settings: `Revalidation:LookAheadHours` (24), `Revalidation:MinAgeMinutes` (60), `Revalidation:IntervalMinutes` (15), `Revalidation:Enabled`.

Booking responses: `201` booked · `400` invalid request · `404` not found · `409` no capacity / conflict ·
`422` not cleared (a TOS or internal check failed — the response lists every check and why).

### Who can do what

| Role | Scope | Can |
|---|---|---|
| CompanyAdmin | own company, any terminal | book / change own company's appointments, create users of own company |
| CompanyUser | own company, any terminal | book / change own company's appointments |
| Planner | own terminal | everything a company can, for any company (on behalf); regenerate slots |
| GateClerk | own terminal | see and change appointments at the terminal |
| TerminalAdmin | own terminal | as Planner, plus create terminal staff and company users |
| SysAdmin | everything | everything |

Company users never see other companies' appointments (they get *not found*). Terminal staff booking on behalf of a company must send `companyId`.

Security: passwords hashed with ASP.NET Core Identity (PBKDF2); 5 wrong passwords lock the account for 15 minutes; login is rate-limited per IP; refresh tokens are stored only as SHA-256 hashes and rotate on every use — re-using an old one signs the user out everywhere.

### Booking rules

- Each request is checked first: categories, container numbers (ISO 6346 format and check digit), cargo references, company / truck / driver ownership, the per-truck daily limit, and no container in two live appointments.
- Then each move is validated against the TOS (availability, customs/line/terminal holds, payment, booking, VGM, cut-off; cargo release for breakbulk). **Any failure rejects the booking**, so trucks never set off for cargo they cannot collect. Warnings book with an amber light.
- Capacity is consumed atomically: one unit of the slot total per truck, one unit of the category quota per move, one trip from the cargo allocation for multipurpose cargo.
- Driver traffic light: **red** (check failed / window passed), **amber** (warning), **green** (inside slot ± grace), **grey** (not yet time, or finished).
- Trucks that have not arrived by slot end + late grace become **no-shows**: capacity is released and the company's no-show count goes up.

### TOS: Navis N4

`Tos:Adapter` selects the TOS: `Fake` (development and demos, configured under `FakeTos`) or `N4`. The fake TOS takes yard positions from `FakeTos:YardPositions` (container → `block.row.bay.tier`), drop / return blocks from `ReceivalBlocks` (category → block) and otherwise picks one of `DefaultBlocks`.
The N4 adapter is **read-only**: each check is one ArgoService `genericInvoke` call running the `TasLookup` Groovy
code extension, which returns units, bookings / EDOs and bills of lading as JSON; TAS turns that into checks
(in the yard, holds, booking left, cut-off, VGM, BL release). Setup, hold mapping and rules:
[`integrations/n4/README.md`](integrations/n4/README.md).

If N4 can't be reached, bookings go ahead with an amber warning and are re-checked as soon as N4 answers
(`Tos:OnUnavailable: Reject` refuses them instead); trip starts are refused and the gate sends the truck to the
trouble desk — a truck is never sent or admitted unchecked.

### Notifications (SMS / WhatsApp / email)

Messages are written to an outbox (`notification_log`) and delivered by a background worker in the recipient's
language (Arabic, English, Urdu, Hindi), branded **Yardsyncs**:

| Message | When | To |
|---|---|---|
| `STATUS_RED` — on hold, don't set off | a re-check finds a hold / unpaid / not available | driver + dispatcher |
| `STATUS_CLEARED` — good to go | a previous hold has cleared | driver + dispatcher |
| `SLOT_REMINDER` — slot time and when the gate opens | `ReminderHoursBefore` (2 h) before the slot | driver |
| `SLOT_CLOSED` — please reschedule | a planner closes booked hours (e.g. crane breakdown) | driver + dispatcher |

Drivers get SMS (or WhatsApp with `DriverChannel: Whatsapp`); dispatchers get email. Drivers signed in to Yardsyncs
mobile get push instead when `PushProvider` is `Fcm` (Firebase; set-up in `mobile/README.md`); a push that can't be
delivered goes by SMS / WhatsApp. Providers per channel:
`SmsProvider` = `Unifonic` · `Twilio` · `Log`, `WhatsappProvider` = `Twilio` · `Log`, `EmailProvider` = `Smtp` · `Log`,
`PushProvider` = `Fcm` · `Log`. Messages: on hold, cleared, reminder, slot closed, and at gate-in the in-gate ticket
with the block(s) to drive to.
**`Log` (the default) only writes the message to the log** — nothing reaches a phone until a provider is configured:

```json
"Notifications": {
  "SmsProvider": "Unifonic", "EmailProvider": "Smtp",
  "Unifonic": { "AppSid": "…", "SenderId": "Yardsyncs" },
  "Smtp": { "Host": "smtp.office365.com", "Port": 587, "Username": "…", "Password": "…", "From": "noreply@…" }
}
```

Delivery is retried after 1, 5, 15 and 60 minutes on temporary errors; invalid numbers / refused senders fail at once;
messages not sent within 12 h are dropped. The sender ID ("Yardsyncs") must be registered with the SMS provider
(CITC rules in Saudi Arabia). Put secrets in environment variables, e.g. `Notifications__Unifonic__AppSid`.

### Driver app

Drivers sign in with the mobile number their company registered; the code arrives by SMS (through the SMS provider in `Notifications`). One person may drive for several companies — the app shows the appointments of every driver record with that number. Driver tokens have their own audience: they open only `/api/driver/*`, and staff / company tokens don't open those.

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/driver/login/start` | `{mobile, installId}` → code by SMS. Same answer for unknown numbers; at most 3 codes per number in 15 minutes |
| `POST` | `/api/driver/login/verify` | `{mobile, installId, code, platform, appVersion}` → session token bound to this phone (signing in elsewhere ends it); 5 tries per code |
| `GET` | `/api/driver/me` | Name, mobile, language, companies |
| `GET` | `/api/driver/appointments` | Upcoming and live appointments (and those finished in the last 12 h): terminal time offset, traffic light and reason, moves, gate code |
| `GET` | `/api/driver/appointments/{no}` | One of them |
| `POST` | `/api/driver/appointments/{no}/start-trip` | The truck is leaving: re-check, then on the way; refused while anything is red |
| `POST` | `/api/driver/logout` | Signs this phone out (and stops pushes to it) |
| `POST` | `/api/driver/appointments/{no}/stops/{seq}/done` | "Done here": the stop is confirmed, the next becomes current; `STOP_COMPLETED` goes to the terminal |
| `PUT` | `/api/driver/push-token` | `{token}`: the app's Firebase push token for this phone (null stops pushes) |
| `GET` | `/api/driver/gate-pass` | Secret for the rotating gate QR `YS1:{passId}:{appointmentNo}:{code}` (8 digits, changes every 30 s, made offline on the phone; revoked on sign-in elsewhere) |
| `POST` | `/api/driver/appointments/{no}/position` | `{latitude, longitude, speed?}` on the way: arrival estimate, terminal areas entered, automatic check-in in the pre-gate area. Inside the terminal (after gate-in): `atStop` when the truck reaches the block of its current stop (within 10 m, slower than ~15 km/h) → `TRUCK_AT_BLOCK` |
| `GET` / `POST` | `/api/terminals/{id}/geofences` | Terminal areas (approach, pre-gate, gate, yard zones…) as GeoJSON; terminal admins draw them as a ring of `[longitude, latitude]` points |
| `PUT` | `/api/terminals/{id}/geofences/{gid}` | Terminal admins: change an area's name, kind, shape, block or check-in flag. Yard blocks need the TOS block name, unique among the terminal's blocks |
| `POST` | `/api/terminals/{id}/geofences/{gid}/deactivate` | Remove an area |
| `GET` | `/api/driver/appointments/{no}/yard?lat&lon` | After gate-in: stops in order (drops, then pickups, then exit) with block and row.bay.tier from the TOS, routes on the terminal's roads, and the yard map; `live: false` when the TOS can't be reached and the last known stops are shown |
| `GET` | `/api/terminals/{id}/control-room` | Live visits (on the way, at the gate, inside) with stage, stops, last app position, time inside, overdue (terminal setting `turn_target_min`, default 60) and no-signal flags; today's averages; the yard map (terminal staff) |
| `GET` | `/api/terminals/{id}/integration-events?limit=` | Events for the terminal's own systems, newest first, with delivery status (staff) |
| `GET` | `/api/terminals/{id}/yard-map` | Areas and roads of the terminal (staff) |
| `GET` | `/api/terminals/{id}/yard-map/roads` | The roads as saved (FeatureCollection with names and one-way flags), for the map editor |
| `PUT` | `/api/terminals/{id}/yard-map/roads` | Terminal admins: `{roads: FeatureCollection}` of LineStrings in `[longitude, latitude]`, `properties.oneWay` for one-way roads (drawn direction); roads join where they share a point |

Gate lanes accept the rotating gate pass in `qrToken` (as well as the static appointment code): the live code is checked and the truck is matched to its appointment. Gate pass secrets are stored encrypted; set `GatePass__Key` (32 bytes, base64) or a key is derived from the JWT signing key.

The Flutter app is in `mobile/` (see `mobile/README.md`). CI analyzes, tests and builds a debug APK, kept for 14 days as a download on the CI run (`yardsyncs-mobile-debug-apk`).

### Bulk booking

Dispatchers can upload a sheet of truck visits instead of booking one by one:

```
visit,date,time,category,container_no,booking_ref,truck_plate,driver_id
V1,2026-10-20,10:00,EXP_DROP,HLXU8700018,BKG1001,1234 ABJ,1012345678
V1,2026-10-20,10:00,IMP_PICKUP,MSCU1234566,,1234 ABJ,1012345678
,2026-10-20,11:00,MTY_RETURN,MAEU4001110,,,
,2026-10-20,12:00,BB_DELIVERY,,,,              (cargo_ref column: BL-STEEL-001)
```

- Columns can be in any order and have common alternative names (`container`, `plate`, `iqama`, `bl`…). Required: `date`, `time` (slot start), `category`.
- Rows with the same `visit` are one truck visit (dual transaction, up to 4 moves).
- `.xlsx` (dates and times may be real Excel cells) or CSV UTF-8 with comma, semicolon or tab. Max 500 rows / 2 MB.
- **`dryRun=true` (the default)** runs every booking check — including TOS — and counts capacity taken by earlier rows of the same file; nothing is booked. Then send `dryRun=false` to book.
- Each visit is booked on its own; failures don't stop the others. The response lists every visit with its rows, status (`Booked` / `WouldBook` / `Failed`), appointment number and reasons.

### Integration events (for the terminal's own systems)

Yardsyncs records what the terminal's TOS, control room or yard management may want to know, and sends it to the
terminal's webhook when the terminal asks for it (per terminal, by terminal code):

| Event | When |
|---|---|
| `TRUCK_CHECKED_IN` | The driver app checked the truck in at the pre-gate |
| `TRUCK_AT_BLOCK` | Inside the terminal, the truck reached the block (or CFS) of its current stop |
| `STOP_COMPLETED` | The driver pressed "Done here" for a stop |

```json
"Integration": {
  "Terminals": {
    "DEMO": { "Url": "https://tos.example/yardsyncs-events", "Events": ["TRUCK_AT_BLOCK", "STOP_COMPLETED"] }
  }
}
```

Secret in an environment variable: `Integration__Terminals__DEMO__Secret`. Each event is a JSON POST with
`eventId`, `type`, `occurredAt`, `terminal`, `appointmentNo`, `truckPlate`, `company`, `position` and, for stop
events, `stop` (`sequence`, `kind` PICKUP / DROP, `block`, `slot` row.bay.tier, `category`, `container`, `bookingRef`).
Headers: `X-Yardsyncs-Event`, `X-Yardsyncs-Delivery` (event id, the same on retries — ignore repeats) and
`X-Yardsyncs-Signature: t={unix seconds},v1={hex HMAC-SHA256 of "{t}.{body}" with the secret}`. Answer 2xx;
other answers are retried (30 s, 2, 10, 30, 60 min) except 4xx. Terminals without a webhook keep the events
(status `SKIPPED`), visible through the API.

### Try it with the demo terminal

In Development the API runs with a fake TOS (`FakeTos` in `appsettings.Development.json`) and offers:

```bash
curl -X POST localhost:5000/api/dev/seed      # demo terminal, company, users, trucks, drivers, cargo + slots
```

Demo logins (password `Demo-Pass-2026`, Development only):

| Email | Role |
|---|---|
| `dispatcher@alnoor.example` | CompanyAdmin, Al Noor Transport |
| `dispatcher@gulfhaulage.example` | CompanyAdmin, Gulf Haulage |
| `planner@demo-terminal.example` | Planner |
| `admin@demo-terminal.example` | TerminalAdmin |

Sign in, then book with the ids the seed returned, e.g.:

```json
POST /api/auth/login
{ "email": "dispatcher@alnoor.example", "password": "Demo-Pass-2026" }

POST /api/terminals/1/appointments          (Authorization: Bearer <accessToken>)
{
  "slotId": 10, "truckId": 1, "driverId": 1,
  "items": [
    { "category": "EXP_DROP",   "containerNo": "HLXU8700018", "bookingRef": "BKG1001" },
    { "category": "IMP_PICKUP", "containerNo": "MSCU1234566" }
  ]
}
```

| Demo data | Result |
|---|---|
| `MSCU1234566`, `MAEU4001110`, `CMAU5100012` | import, all clear |
| `MSCU7654329` | customs hold → rejected |
| `MAEU4002225` | not yet available → rejected |
| `TGHU3000022` | line hold → rejected |
| `OOLU6200024` | unpaid → rejected |
| `CMAU5100028` | storage warning → booked, amber |
| `HLXU8700018` + any booking | export, all clear |
| `HLXU8700023` | missing VGM → rejected |
| booking `BKG9999` / `BKG0000` | cut-off passed / invalid booking |
| cargo `BL-STEEL-001` (`BB_DELIVERY`) | 40 trips released |
| cargo `BL-PIPES-002` | not released by customs → rejected |

## Status

- [x] Data model and schema
- [x] Capacity engine: templates, seasonal templates, overrides, overbooking, quota release, reservations
- [x] Slot generation that preserves bookings and flags over-capacity
- [x] Race-safe booking counters
- [x] Booking API: book, cancel, reschedule, assign truck/driver, dual transactions, multipurpose cargo
- [x] TOS validation through an adapter interface, with a configurable fake
- [x] Driver traffic light and automatic no-shows
- [x] Authentication and roles: login, lockout, rotating refresh tokens, company isolation, terminal scope, user creation
- [x] Capacity admin API: templates, seasonal templates, overrides, reservations, release rules, settings, slot board, audit log
- [x] Master data API: companies, trucks, drivers (Arabic plates, mobile normalization, blocking), categories, cargo releases, user management
- [x] Bulk booking: Excel / CSV upload with dry-run preview, dual visits, per-row results, templates
- [x] Re-checks before arrival: scheduled, on request and at trip start; history and notification outbox
- [x] Gate: lane arrivals (QR / number / plate / container), re-check, admit or trouble desk, walk-ins, gate in/out, turn time, gate board
- [x] Navis N4 adapter (read-only checks via genericInvoke + Groovy code extension), outage handling
- [x] Notifications: on hold / cleared / reminder / slot closed in 4 languages; Unifonic, Twilio (SMS, WhatsApp), SMTP; retries
- [x] Web portal, round 1: sign-in, booking, appointments, slot board with capacity changes, gate and trouble desk; Arabic / English
- [x] Web portal, round 2: capacity setup (weekly plan grid, seasonal plans, reserved places, rules), bulk upload, fleet and users, KPI dashboard
- [x] Driver app, part 1: sign-in by SMS code, my appointments with traffic light, gate QR code, start trip (Arabic, English, Urdu, Hindi)
- [x] Rotating gate pass, positions on the way with arrival estimate, automatic check-in at the pre-gate, terminal areas
- [x] Yard navigation: container's yard position from the TOS, routes on the terminal's roads, where-to-go screen in the app
- [x] Terminal map editor in the portal: areas and roads, GeoJSON import, reachability check, route preview
- [x] Driver app: trip position in the background (Android foreground service, iOS background location), spoken directions in the yard
- [ ] Driver app, next: push notifications, app identity and store release
