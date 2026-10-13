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
docs/                         design documents (start with docs/data-model.md)
db/schema.sql                 PostgreSQL + PostGIS schema
src/Tas.Domain                entities
src/Tas.Application           capacity engine (pure logic: planning, availability, reconciliation)
src/Tas.Infrastructure        EF Core + Npgsql, slot generation, availability queries, booking counters
src/Tas.Api                   ASP.NET Core minimal API
tests/Tas.Application.Tests   unit tests for the capacity rules
tests/Tas.Infrastructure.Tests integration tests against PostgreSQL (run when TAS_TEST_DB is set)
```

## Running locally

Requires the .NET 10 SDK and PostgreSQL 15+ with PostGIS.

```bash
createdb tas
psql -d tas -f db/schema.sql

# set ConnectionStrings:Tas in src/Tas.Api/appsettings.json (or an environment variable)
dotnet run --project src/Tas.Api
```

Tests:

```bash
dotnet test tests/Tas.Application.Tests

# integration tests need a database created from db/schema.sql
export TAS_TEST_DB="Host=localhost;Database=tas_test;Username=postgres;Password=postgres"
dotnet test tests/Tas.Infrastructure.Tests
```

CI (GitHub Actions) builds the solution and runs both test projects against a PostGIS container on every push.

## API (so far)

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/terminals/{id}/slots/generate?from=2026-10-20&to=2026-10-27` | Regenerate slots after changing templates or overrides (local dates) |
| `GET` | `/api/terminals/{id}/availability?date=2026-10-20&category=IMP_PICKUP&companyId=1` | Slots of a day with remaining capacity for a category |
| `POST` | `/api/terminals/{id}/appointments` | Book a truck visit (1–4 moves; 2+ = dual transaction) |
| `GET` | `/api/terminals/{id}/appointments/{no}` | One appointment with checks and live traffic light |
| `GET` | `/api/terminals/{id}/appointments?date=2026-10-20&companyId=1` | Appointments of a day |
| `POST` | `/api/terminals/{id}/appointments/{no}/cancel` | Cancel (until the cancellation cut-off; terminal staff any time) |
| `POST` | `/api/terminals/{id}/appointments/{no}/reschedule` | Move to another slot (allowed until gate-in, e.g. for late trucks) |
| `PUT` | `/api/terminals/{id}/appointments/{no}/assignment` | Assign / change truck and driver |
| `GET` | `/health` | Health check |

Background workers: slot generation (every 6 h) and no-show marking (every 10 min).

Booking responses: `201` booked · `400` invalid request · `404` not found · `409` no capacity / conflict ·
`422` not cleared (a TOS or internal check failed — the response lists every check and why).

> `userId` is passed in request bodies for now; it will come from authentication.

### Booking rules

- Each request is checked first: categories, container numbers (ISO 6346 format and check digit), cargo references, company / truck / driver ownership, the per-truck daily limit, and no container in two live appointments.
- Then each move is validated against the TOS (availability, customs/line/terminal holds, payment, booking, VGM, cut-off; cargo release for breakbulk). **Any failure rejects the booking**, so trucks never set off for cargo they cannot collect. Warnings book with an amber light.
- Capacity is consumed atomically: one unit of the slot total per truck, one unit of the category quota per move, one trip from the cargo allocation for multipurpose cargo.
- Driver traffic light: **red** (check failed / window passed), **amber** (warning), **green** (inside slot ± grace), **grey** (not yet time, or finished).
- Trucks that have not arrived by slot end + late grace become **no-shows**: capacity is released and the company's no-show count goes up.

### Try it with the demo terminal

In Development the API runs with a fake TOS (`FakeTos` in `appsettings.Development.json`) and offers:

```bash
curl -X POST localhost:5000/api/dev/seed      # demo terminal, company, users, trucks, drivers, cargo + slots
```

Then book with the ids it returns, e.g.:

```json
POST /api/terminals/1/appointments
{
  "companyId": 1, "userId": 1, "slotId": 10, "truckId": 1, "driverId": 1,
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
- [ ] Authentication and roles
- [ ] Navis N4 adapter
- [ ] Operator portal (React)
- [ ] Driver app (Flutter)
