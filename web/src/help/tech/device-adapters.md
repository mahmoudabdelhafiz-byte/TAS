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
| `Call`, `Hangup` | Intercoms | none: connect the lane intercom to the clerk desk, end the call |

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

## The lane controller

At a lane set to **Automatic**, Yardsyncs runs the lane itself, one transaction per truck:

1. **Gather.** Reads at the lane within 30 seconds of each other belong to the same truck, unless the plate is
   different. The most confident plate wins, and the containers from all cameras add up.
2. **Decide.** The controller decides once the lane has been quiet for 3 seconds, so the front and rear cameras have
   both reported; a kiosk entry or a scanned gate pass is decided at once. The decision is the same as the gate's for a
   clerk: match the appointment, re-check with the TOS, check the arrival window, the holds and any blocks.
3. **Act.**
   - Let in: `Green`, `Open` and a welcome on the display. When the barrier confirms `Open`, the truck is gated in;
     at an entry lane the TOS's yard position then goes to the display (`Go to B2 (14.03.2)`) and the gate ticket,
     with the position, to the printer. The driver app gets the same stops by push.
   - With the terminal setting `seal_hold`, a seal read at the entry before the decision that isn't the one the company
     declared refers the truck (`SEAL_MISMATCH`); without it, the mismatch is only recorded in the inspection.
   - Referred: `Red`, and the reason on the display. The case goes to the trouble desk, and when a clerk lets the truck
     in there, the lane's barrier opens.
   - Exit lanes gate the truck out (by plate or appointment number) and open, or call a clerk. Before that, the
     containers the cameras saw are checked against the visit: what the truck came to collect must be on it, what it
     came to drop must not be, nothing else may be. A problem holds the truck on red (`EXIT_CHECK`) until a clerk lets
     it out with a note (`POST …/transactions/{id}/let-out`). With weighbridge reads at the entry and the exit, a single
     cargo move gets its actual tonnage from the difference.

A transaction with nothing to identify the truck by is given up after 2 minutes. Manual lanes and lanes that aren't
open are left to the clerk; their reads are still stored and shown. The timings are settings: `LaneController__SettleSeconds`
(3), `LaneController__WindowSeconds` (30), `LaneController__AbandonSeconds` (120), and `LaneController__Enabled`.
`GET /api/terminals/{id}/gate/transactions?laneId=` lists the trucks a lane handled; `GET …/transactions/{id}` gives
one truck's reads, photos, commands and trouble case. `POST …/transactions/{id}/recheck` with corrected `plate`,
`containers` or `appointmentNo` has the lane check a referred truck again (the remote clerk station uses it). Photos
show in the station when they are `https://` addresses the clerk's browser can reach.

The controller runs inside Yardsyncs. When Yardsyncs is installed at the terminal, it sits next to the lanes and
doesn't depend on any outside link (only on the TOS, as a clerk does). If the TOS can't be reached, no truck is let in
automatically: it is referred for a clerk to check.

## Edge mode: lanes that keep working when the cloud is out of reach

When Yardsyncs is hosted in the cloud, a small **lane agent** runs at the terminal (`deploy/edge/`), next to the lanes.
The devices talk to the agent instead of the cloud, with the same device API and the same keys:

- **While the cloud answers**, the agent passes every call through, and the cloud runs the lanes as described above.
- **When it doesn't** (no answer within 3 seconds), the agent runs the automatic lanes itself from a snapshot it took
  in the last few minutes: the lanes and device keys (fingerprints only), the appointments of the next 36 hours with
  their plates, containers and last "ready to go" light, the drivers' gate passes, and the terminal's gate rules. It
  matches and decides with the same rules as the cloud, and sends the same commands.
- **When the cloud is back**, the agent sends what it decided (`POST …/gate/edge/passages`): trucks let in are gated
  in at the time the barrier opened, trucks let out are gated out, referrals are logged. Each one is recorded once,
  however often it is sent. The TOS then gets the gate transactions and the drivers their yard position.

What the agent can't do on its own: ask the TOS again (it uses the last light the cloud saw; a truck that was red stays
referred), give a yard position, check exits against the containers, or put cases on the trouble desk. A truck it
refers is dealt with at the lane, and the referral shows in the gate log afterwards. A truck let in offline whose
barrier never confirmed is recorded as arrived, for the gate staff to check.

### When something fails

The lanes fail towards a person, never towards an open barrier:

| What fails | Automatic entry lanes | Exit lanes | Afterwards |
|---|---|---|---|
| The TOS | No truck is let in automatically: each is referred for a clerk to check in the TOS | Trucks leave as usual (the visit is closed in Yardsyncs) | Gate records for the TOS wait and are sent again |
| The cloud (edge mode) | The lane agent decides with the last light the cloud saw: red stays referred, nothing new is cleared | Trucks known to be inside leave; others wait for a clerk | The agent sends what it decided; each passage is recorded once |
| The lane agent itself | The devices get no answer: the lane is worked by a clerk as a manual lane | Same | Restart the agent; nothing it decided is lost |
| A camera or OCR | Trucks are identified at the kiosk (appointment number or gate pass), or referred | Referred | The device shows **Offline** or **Fault** on the Lanes screen |
| The barrier | A barrier that doesn't confirm the opening refers the truck: a clerk lets it through by hand | Same | The visit is gated in only when the barrier confirms |
| A command arrives late | Barrier and light commands expire after 30 seconds and are not carried out late | Same | The clerk sees the expired command |

The agent signs in to the cloud with a gate system key of the terminal (**Lanes → Gate systems**); only such a key can
take the snapshot (`GET …/gate/edge/snapshot`) or send passages. `GET /edge/status` on the agent tells whether it is
online, how old its snapshot is and how many passages it still has to send.

## A reference adapter

`deploy/device-adapter/simulator.mjs` acts as a camera and a barrier with no other software needed. It reads a plate,
reports status, and opens the barrier when told to. Use it to try the API, or as the starting point for an adapter:

```sh
YARDSYNCS_URL=https://yard.example.com CAMERA_KEY=ysd_… BARRIER_KEY=ysd_… node deploy/device-adapter/simulator.mjs "1234 ABJ"
```
