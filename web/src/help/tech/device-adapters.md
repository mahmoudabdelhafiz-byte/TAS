# Connecting lane devices

The Autogate works with any make of camera, OCR engine, RFID reader, kiosk, barrier, traffic light, display or
printer. Each device, or the vendor's own adapter software speaking for it, talks to Yardsyncs over one small HTTPS
API. The device always calls out to Yardsyncs and never waits for Yardsyncs to call it, so nothing on the terminal's
network has to accept incoming connections. This page is for the integrator who connects the devices.

## Before you start

1. A terminal admin records the device on the **Lanes** screen. Each device needs a code (e.g. `IN1-CAM-FRONT`), a
   kind, and what it reads.
2. The admin chooses **Device key**. The key (`ysd_…`) is shown once, so copy it into the device or adapter
   configuration then. Yardsyncs keeps only its fingerprint. Choosing **Device key** again issues a new key, and the
   old one stops working at once.
3. Send the key in the `X-Device-Key` header on every call. `GET /api/devices/me` answers with the device, its lane
   and its terminal, so you can check the key works.

One key belongs to one device. An adapter that serves several devices (one OCR server for four cameras, say) keeps
one key per device and uses each device's own key for that device's calls.

## The calls

| Method | Path | What it is for |
|---|---|---|
| `GET` | `/api/devices/me` | Which device this key belongs to |
| `POST` | `/api/devices/status` | `{ "status": "Ok" }` or `{ "status": "Fault", "detail": "Lens dirty" }`. Send it every 30 seconds |
| `POST` | `/api/devices/reads` | What the device read (below) |
| `GET` | `/api/devices/commands` | The commands to carry out now, oldest first. Ask every second |
| `POST` | `/api/devices/commands/{id}/ack` | `{ "done": true }`, or `{ "done": false, "detail": "Arm blocked" }` |

Any call counts as a sign of life. A device that hasn't called for two minutes shows as **Offline** at the gate.
Calls are limited to 600 a minute per device (setting `Devices__PerMinute`).

## Reads

Send only what the device produces. A read with fields the kind of device can't produce is refused with 400.

| Kind | Fields |
|---|---|
| Camera / OCR | `plate`, `plateConfidence` (0–100), `containers` (up to 4: `number`, `isoCode`, `confidence`, `position` such as FRONT / REAR), `seals`, `damage` (`photo`, `note`, `side`), `photos` |
| RFID reader | `tag` |
| Kiosk | `appointmentNo`, or `qrToken` (the gate pass scanned from the driver app) |
| Weighbridge | `weightKg` |

Every read may carry `readAt`, the device's own time. It is refused if it is more than 5 minutes ahead of Yardsyncs or
a day behind, so keep device clocks on NTP.

```http
POST /api/devices/reads
X-Device-Key: ysd_…
Content-Type: application/json

{ "plate": "1234 ABJ", "plateConfidence": 93,
  "containers": [ { "number": "MSCU1234566", "isoCode": "22G1", "position": "FRONT" } ],
  "photos": [ "https://ocr.terminal.local/img/7731.jpg" ] }
```

Yardsyncs normalizes what you send. Plates are put in the form the gate matches on, Arabic letters included, and
container numbers are upper-cased with the ISO 6346 check digit tested. The answer (201) is the read as stored, with
`warnings` for anything that looks misread, such as a container whose check digit doesn't match. Photos are references
(URLs or file keys) to pictures your system keeps; Yardsyncs doesn't store the pictures. Reads are deleted after the
privacy period for events (90 days by default).

## Commands

| Command | Goes to | Text |
|---|---|---|
| `Open`, `Close` | Barriers | none |
| `Red`, `Green`, `Off` | Traffic lights | none |
| `Show`, `Clear` | Displays and kiosks | `Show` carries the text to display |
| `Print` | Ticket printers | the ticket text |

A command lists its `expiresAt`. Barrier and light commands live for 30 seconds, and display and print commands for
2 minutes. Never carry out a command after it expires: a barrier that opens late, for whichever truck is there by then,
is dangerous. Yardsyncs doesn't hand out expired commands. A command keeps coming back in `GET /api/devices/commands`
until you confirm it, so carry out each command id only once, then confirm it.

```http
GET /api/devices/commands
→ [ { "commandId": 812, "command": "Open", "text": null, "createdAt": "…", "expiresAt": "…" } ]

POST /api/devices/commands/812/ack
{ "done": true }
```

Gate staff can send commands to a lane from the gate screen (**Open barrier**) or with
`POST /api/terminals/{id}/gate/lanes/{laneId}/commands`. Name a device with `deviceCode` to reach only that one;
without it, the command goes to every device at the lane that carries it out. The lane controller, which decides
automatically, sends its commands the same way. `GET …/lanes/{laneId}/activity` shows the lane's latest reads and
commands, and how far each command got: waiting, delivered, done, failed or expired.

## A reference adapter

`deploy/device-adapter/simulator.mjs` acts as a camera and a barrier with no other software needed. It reads a plate,
reports status, and opens the barrier when told to. Use it to try the API, or as the starting point for an adapter:

```sh
YARDSYNCS_URL=https://yard.example.com CAMERA_KEY=ysd_… BARRIER_KEY=ysd_… node deploy/device-adapter/simulator.mjs "1234 ABJ"
```
