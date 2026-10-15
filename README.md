# TAS — Truck Appointment System

Truck appointment and landside management for **container and multipurpose terminals**.
Multi-terminal by design: one deployment serves many terminals.

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
```

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

Requires Node.js 22.

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
| Company admin / dispatcher | Book a truck, Appointments (own company) |
| Planner, terminal admin | Slot board (with capacity changes), Appointments (all companies), Book a truck (on behalf of a company), Gate |
| Gate clerk | Gate (board, truck check, gate in/out, trouble desk), Appointments |

Arabic and English, right-to-left in Arabic. Times are shown in the terminal's time zone. Messages that come from the server (check results, errors) are in English for now.

## API (so far)

All endpoints except `/health` and login/refresh/logout need `Authorization: Bearer <accessToken>`.

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/auth/login` | Email + password → access token (30 min), refresh token (14 days), profile |
| `POST` | `/api/auth/refresh` | New token pair; the old refresh token stops working |
| `POST` | `/api/auth/logout` | Revoke a refresh token |
| `GET` | `/api/auth/me` | Signed-in user's profile |
| `POST` | `/api/auth/change-password` | Change password; signs out all other sessions |
| `POST` | `/api/users` | Create a user (within the admin's scope) |
| `POST` | `/api/terminals/{id}/slots/generate?from=2026-10-20&to=2026-10-27` | Regenerate slots after changing templates or overrides (local dates) |
| `GET` | `/api/terminals/{id}/availability?date=2026-10-20&category=IMP_PICKUP&companyId=1` | Slots of a day with remaining capacity for a category |
| `POST` | `/api/terminals/{id}/appointments` | Book a truck visit (1–4 moves; 2+ = dual transaction) |
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
| `GET` / `POST` / `PUT` | `/api/companies/{id}/drivers[/{driverId}]` | as trucks | Drivers; mobiles normalized to `+966…` |
| `POST` | `/api/companies/{id}/drivers/{driverId}/block` | TerminalAdmin | Block / unblock |
| `GET` / `POST` / `PUT` | `/api/terminals/{id}/categories[/{categoryId}]` | everyone at the terminal / TerminalAdmin | Transaction categories; new ones are bookable at once |
| `GET` / `POST` / `PUT` | `/api/terminals/{id}/cargo[/{cargoId}]` | companies see own / Planner, TerminalAdmin | Multipurpose cargo releases (BL / DO) with trip and tonnage limits |
| `GET` | `/api/users` | CompanyAdmin, TerminalAdmin, SysAdmin | Users within your scope |
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

`Tos:Adapter` selects the TOS: `Fake` (development and demos, configured under `FakeTos`) or `N4`.
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

Drivers get SMS (or WhatsApp with `DriverChannel: Whatsapp`); dispatchers get email. Providers per channel:
`SmsProvider` = `Unifonic` · `Twilio` · `Log`, `WhatsappProvider` = `Twilio` · `Log`, `EmailProvider` = `Smtp` · `Log`.
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
- [ ] Web portal, round 2: weekly template editor, bulk upload, fleet and users, KPI dashboard
- [ ] Driver app (Flutter)
