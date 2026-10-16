# System overview

Yardsyncs is a truck appointment system for container and multipurpose terminals. Trucking companies book truck
visits into hourly slots; the terminal checks every container with its terminal operating system (TOS) before and
after booking, works the gate, and follows each truck through the yard; drivers use the Yardsyncs mobile app.

## Parts

| Part | Who uses it | What it does |
|---|---|---|
| Web portal | Trucking companies, planners, gate clerks, terminal admins | Booking, tracking containers, appointments, capacity planning, gate, control room, KPIs, fleet and users, help |
| Yardsyncs mobile (Android, iPhone) | Drivers | Appointments, "ready to go", leaving for the terminal, rotating gate code, in-gate ticket, directions in the yard with voice, "done here" |
| Server (API) | The portal, the app, the terminal's systems | Rules, checks with the TOS, notifications, events for the terminal's systems, background jobs |
| Database | The server | PostgreSQL 16 with PostGIS (maps and positions) |

## A truck visit, end to end

1. **Capacity.** The terminal's weekly plan creates slots: trucks per hour, limits per move type, places held for
   walk-ins or a company.
2. **Booking.** A company books a slot with its moves. Each container is checked with the TOS (in the yard, customs,
   line release, terminal holds, charges). Nothing is booked unless every check passes.
3. **Before the visit.** Booked containers are checked again; a hold or a release sends the driver an SMS and the
   booker an email. Reminders go out before the hour.
4. **On the way.** The driver presses "I am leaving now"; the app reports positions; the terminal sees the arrival
   estimate; the truck is checked in when it enters the pre-gate.
5. **Gate.** The clerk (or an automated gate) scans the driver's rotating gate code or reads the plate. The visit is
   re-checked on the spot: admitted, or sent to the trouble desk with the reason.
6. **Yard.** The app shows the block (and row, bay, tier) of each container and the route on the terminal's roads;
   reaching the block and "done here" are recorded and can be sent to the terminal's systems.
7. **Out.** Gate-out completes the visit; turn time and time per block feed the KPIs.

## Integration with the terminal

| With | How |
|---|---|
| Terminal operating system | Navis N4 through its web services (an adapter per TOS); a demo adapter for training |
| The terminal's own systems | Events by webhook: truck checked in, truck at block, stop completed (see Integration events) |
| Automated gates (OCR, RFID, kiosks) | The gate API, as a gate clerk account |
| SMS, WhatsApp, email, push | Unifonic or Twilio, SMTP, Firebase Cloud Messaging |

## Hosting

The same server image runs in our cloud service (one installation for several terminals) or on a server at the
terminal (on-premise). It needs a Linux server with Docker, 4 CPU and 8 GB of memory for a mid-size terminal, and an
HTTPS address the drivers' phones can reach. See Operations.

## Languages

Portal: Arabic and English. Driver app and messages to drivers: Arabic, English, Urdu and Hindi.
