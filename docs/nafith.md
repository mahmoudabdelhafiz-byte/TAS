# Connecting Yardsyncs to Nafith (zone truck platform)

In an economic zone such as Egypt's SCZone, **Nafith** runs the truck side of the zone: entry permits, driver and
vehicle verification, the marshalling (truck) yard and its gate, and customs clearance from **Nafeza**. Yardsyncs works
next to it: appointments and capacity at the terminals, the driver app (trip, yard directions, messages, safety), the
control room and reporting. Nothing is entered twice.

```
Trucking company ──books──▶ Yardsyncs ──pre-announcement──▶ Nafith ──permit, yard, Nafeza──▶ truck enters the zone
                               ▲   │                          │
                               │   └──asks: cleared? permit?──┘
                               └──────── yard events ◀────────┘
```

## The three flows

| Flow | Direction | When | What |
|---|---|---|---|
| **Checks** | Yardsyncs → Nafith | Booking, every re-check before arrival, and the gate | Customs clearance per container (Nafeza through Nafith); at the gate, the truck's and driver's entry permit |
| **Pre-announcement** | Yardsyncs → Nafith | A visit is booked, moved, gets its truck or driver, or is cancelled | Events `APPOINTMENT_BOOKED`, `APPOINTMENT_CHANGED`, `APPOINTMENT_CANCELLED` with the hour, truck plate, driver (name, ID number, mobile) and containers |
| **Yard events** | Nafith → Yardsyncs | The truck enters or leaves the zone's yard, a permit is withdrawn, customs status changes | `POST /api/zone/events` (below) |

**What the checks do**
- A container Nafith reports as **not cleared** is refused at booking, with the reason ("not cleared by customs in NAFITH (…)"). A container Nafith doesn't know yet, or Nafith not answering, gives a warning: the booking goes ahead and the container is re-checked before the truck comes.
- At the gate, a truck **without a valid entry permit** goes to the trouble desk ("No valid NAFITH entry permit for …"). If Nafith can't be reached, the clerk sees a warning; the zone's own gate still checks the permit.

## Configuration

```
Nafith__Mode=Live                      # Off (default) | Simulator | Live
Nafith__Terminals__0=SOK1              # terminal codes in the zone (none listed = every terminal)
Nafith__BaseUrl=https://api.nafith.example
Nafith__ApiKey=…                       # secret: environment variable only
Nafith__ApiKeyHeader=X-Api-Key
Nafith__ClearancePath=/api/v1/containers/{container}/clearance
Nafith__PermitPath=/api/v1/permits/check?plate={plate}&driverId={driverId}&at={at}
Nafith__InboundKey=…                   # secret Nafith sends in X-Zone-Key

# Pre-announcements go out through the terminal's webhook (signed, retried; see the technical docs):
Integration__Terminals__SOK1__Url=https://api.nafith.example/yardsyncs/events
Integration__Terminals__SOK1__Secret=…
Integration__Terminals__SOK1__Events__0=APPOINTMENT_BOOKED
Integration__Terminals__SOK1__Events__1=APPOINTMENT_CHANGED
Integration__Terminals__SOK1__Events__2=APPOINTMENT_CANCELLED
```

**Simulator** (`Nafith__Mode=Simulator`) answers without a network, for demos and testing before Nafith's test
environment is available: containers in `Nafith__SimulatorNotCleared__*` are not cleared, plates in
`Nafith__SimulatorNoPermit__*` have no permit, everything else is fine.

## What Yardsyncs expects from Nafith's API (to be confirmed)

Nafith's API is not public. Until its documentation is shared, the connector expects the answers below; the paths are
settings, and the readers also accept common variants (`state`, `cleared: true/false`, `reason`). When Nafith's real
API differs, only the paths and the two readers in `NafithZoneSystem` change.

**Customs clearance:** `GET {ClearancePath}` → 200
```json
{ "status": "CLEARED" | "NOT_CLEARED" | "UNKNOWN", "message": "Inspection pending", "reference": "ACI/Nafeza ref" }
```
404 = Nafith doesn't know the container (a warning, re-checked later).

**Entry permit:** `GET {PermitPath}` → 200
```json
{ "valid": true, "permitNo": "NP-123", "validUntil": "2026-10-21T18:00:00Z", "reason": "Permit expired" }
```

## Yard events from Nafith: `POST /api/zone/events`

Header `X-Zone-Key: {Nafith__InboundKey}`; JSON body:

```json
{ "type": "YARD_ENTERED", "terminal": "SOK1", "appointmentNo": "SOK1-261021-4KQ2M", "plate": "…", "containerNo": "…", "message": "Bay 14" }
```

| type | Yardsyncs does |
|---|---|
| `YARD_ENTERED` | Notes it on the visit ("In the zone's truck yard"); a booked visit becomes **on the way** (the control room counts it as coming) |
| `YARD_RELEASED` | Notes it ("Released from the zone's truck yard to the terminal") |
| `PERMIT_REVOKED` | Notes it; the gate refuses the truck when it checks the permit |
| `CLEARANCE_CHANGED` | Re-checks at once every live visit with `containerNo` (customs released or put on hold) |

The visit is found by `appointmentNo`, else by `plate` (the live visit nearest in time, within a day). Answers:
200 with the appointment numbers, 400 for an unknown type or terminal, 401 without the key, 404 when no visit matches.

## Needed from the customer / Nafith

1. Nafith's API documentation and a test environment with credentials.
2. Which of the three flows they want, and who owns the driver and truck lists.
3. Nafith's address for our pre-announcements, or an agreement that Nafith reads them from our webhook format.
