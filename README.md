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
| `GET` | `/api/terminals/{id}/availability?date=2026-10-20&category=IMP_PICKUP&companyId=12` | Slots of a day with remaining capacity for a category |
| `GET` | `/health` | Health check |

A background worker also regenerates every active terminal's slots up to its booking horizon (every 6 hours by default).

## Status

- [x] Data model and schema
- [x] Capacity engine: templates, seasonal templates, overrides, overbooking, quota release, reservations
- [x] Slot generation that preserves bookings and flags over-capacity
- [x] Race-safe booking counters
- [ ] Booking API (appointments, items, TOS validation)
- [ ] N4 adapter (fake first, then real)
- [ ] Operator portal (React)
- [ ] Driver app (Flutter)
