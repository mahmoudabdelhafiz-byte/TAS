# Connecting your own gate system

Yardsyncs comes as two modules. The **truck appointment system** books visits, checks them with the terminal
operating system (TOS), and decides at the gate whether a truck may come in. **Yardsyncs Autogate** runs the gate
itself: lane devices, automatic lanes, the remote clerk, inspection, gate tickets, the exit check and writing gate
transactions to the TOS. A terminal can have both, or only the truck appointment system and keep the gate system it
already has (an OCR portal, kiosks, a gate operating system). This page is for that second case.

## What your gate system does, and what Yardsyncs does

| Your gate system | Yardsyncs |
|---|---|
| Reads plates, containers and gate passes at its lanes | Matches the reads to an appointment |
| Opens barriers, prints tickets, shows directions | Re-checks the visit with the TOS and decides: admit, or refer to the trouble desk |
| Records the gate transaction in the TOS (if it does today) | Keeps the visit's status, the gate board, the control room and the KPIs up to date |
| Tells Yardsyncs when a truck went in and out | Tells your systems about trucks in the terminal by webhook (see *Integration events*) |

## 1. Lanes and a key

A terminal admin opens **Lanes** in the portal:

- Add each lane your gate system uses, with a code (`IN-1`, `OUT-1`) and its direction. Your gate system names the
  lane in every call.
- Under **Gate systems**, add your gate system by name. The key is shown once: copy it into your gate system's
  settings. Yardsyncs keeps only a fingerprint; if the key is lost, revoke it and add a new one.

Each key acts as its own gate clerk account: it may do what a gate clerk of that terminal may do, and the visit
history shows the gate system's name. It can't sign in to the portal. Revoking the key stops it at once.

## 2. Calls

Send the key in the `X-Api-Key` header, over HTTPS:

```
X-Api-Key: ysg_…
```

| Method | Path | When |
|---|---|---|
| `POST` | `/api/terminals/{id}/gate/arrivals/check` | Optional: what Yardsyncs would decide, recording nothing |
| `POST` | `/api/terminals/{id}/gate/arrivals` | A truck at an entry lane: the decision, recorded |
| `POST` | `/api/terminals/{id}/gate/in` | The barrier opened for an admitted truck |
| `POST` | `/api/terminals/{id}/gate/out` | The truck left |
| `GET` | `/api/terminals/{id}/appointments?date=2026-10-20` | The day's appointments, to show or preload |
| `GET` | `/api/terminals/{id}/appointments/{no}` | One appointment with its "ready to go" light |
| `GET` | `/api/terminals/{id}/gate/troubles?openOnly=true` | Trucks waiting at the trouble desk |

An arrival sends what the lane read; send what you have:

```
POST /api/terminals/7/gate/arrivals
X-Api-Key: ysg_…
{"laneCode": "IN-1", "plateRead": "1234 ABJ", "containerReads": ["MSCU1234566"], "qrToken": null, "appointmentNo": null}

→ 200 {"data": {"decision": "Admit", "message": "...", "appointmentNo": "JED1-261020-7KQ4M", "matchedBy": "Plate",
               "light": "Green", "troubleId": null, "reason": null, "warnings": [], "appointment": {...}}}
```

`decision` is `Admit` or `Refer`. A referred truck has a `reason` (`Early`, `Late`, `NotCleared`, `Blocked`,
`AlreadyIn`, `Cancelled`, `NoAppointment`, `DriverUnknown`) and a `troubleId`: a gate clerk sees it at the trouble desk, and may
admit it from there.

Then, when the barrier opened and when the truck left:

```
POST /api/terminals/7/gate/in    {"appointmentNo": "JED1-261020-7KQ4M", "laneCode": "IN-1"}
POST /api/terminals/7/gate/out   {"appointmentNo": "JED1-261020-7KQ4M", "laneCode": "OUT-1"}
```

`arrivals/check` takes the same body as `arrivals` and answers the same way, but records nothing: no arrival, no
trouble-desk case, no gate event. Use it to show the driver an answer before the truck is at the barrier, for
example at a pre-gate kiosk. It still asks the TOS, so the visit's "ready to go" light may be refreshed.

## 3. Errors

| Status | Meaning |
|---|---|
| `401` | The key is missing, unknown or revoked |
| `403` | The key belongs to another terminal |
| `400` | The lane code isn't an active lane of the right direction, or the body is incomplete |
| `409` | The lane is closed, or the truck isn't in a state for this step (e.g. gate-out before gate-in) |

Errors are `application/problem+json` with `detail`.

## Without Autogate

Without Yardsyncs Autogate, the portal has no lane devices, automatic lanes, remote clerk, inspection or gate
tickets, and Yardsyncs doesn't write gate transactions to the TOS: your gate system keeps doing what it does today.
The trucks are still checked against the TOS at the gate, and the visit, the gate board, the control room, the
driver app and the KPIs work as with Autogate. Adding Autogate later needs no change to bookings or to your gate
system's calls.
