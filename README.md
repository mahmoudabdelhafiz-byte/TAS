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
| Database | SQL Server |

## Repository layout

```
docs/   design documents (start with docs/data-model.md)
db/     database schema and migrations
src/    application source (to come)
```

## Status

Design phase — see `docs/data-model.md` for the data model, capacity logic and open decisions.
