# Integration events

Yardsyncs tells the terminal's own systems (TOS, yard management, control room tools) what happens to trucks, as
they happen, by HTTPS webhook. Each terminal chooses whether it wants them and which ones.

## Events

| Event | When |
|---|---|
| `TRUCK_CHECKED_IN` | The driver app checked the truck in at the pre-gate |
| `TRUCK_AT_BLOCK` | Inside the terminal, the truck reached the block (or warehouse) of its current stop |
| `STOP_COMPLETED` | The driver pressed "Done here" for a stop |

## What is sent

A JSON `POST` to the terminal's address:

```
{
  "eventId": 4211,
  "type": "TRUCK_AT_BLOCK",
  "occurredAt": "2026-10-20T07:42:10Z",
  "terminal": "JED1",
  "appointmentNo": "JED1-261020-7KQ4M",
  "truckPlate": "1234 ABJ",
  "company": "Al Noor Transport",
  "position": [39.1665, 21.4640],
  "stop": {
    "sequence": 1, "kind": "PICKUP", "block": "B2", "slot": "14.03.2",
    "category": "IMP_PICKUP", "container": "MSCU1234566", "bookingRef": null
  }
}
```

`position` is `[longitude, latitude]`. `stop` is in the stop events.

Headers:

| Header | Value |
|---|---|
| `X-Yardsyncs-Event` | The event type |
| `X-Yardsyncs-Delivery` | The event id: the same on every retry, so a repeat can be ignored |
| `X-Yardsyncs-Signature` | `t={unix seconds},v1={hex HMAC-SHA256 of "{t}.{body}" with the shared secret}` |

To check a delivery: recompute the HMAC over the timestamp, a dot and the raw body with the secret, compare it with
`v1` in constant time, and refuse timestamps older than a few minutes.

## Delivery

Answer with any `2xx` once the event is stored. Other answers and timeouts (10 seconds) are retried after 30
seconds, 2, 10, 30 and 60 minutes, up to 8 tries within 24 hours; `4xx` answers (except 408 and 429) are not
retried. Events wait in an outbox in the database, so nothing is lost while the terminal's system is down.
Terminals without a webhook keep the events for 90 days, visible to their staff at
`GET /api/terminals/{id}/integration-events`.

## Setting it up

Per terminal code, in the server's settings (the secret as an environment variable):

```
Integration__Terminals__JED1__Url=https://tos-gateway.terminal.com/yardsyncs-events
Integration__Terminals__JED1__Secret=<random, at least 32 characters>
Integration__Terminals__JED1__Events__0=TRUCK_AT_BLOCK
Integration__Terminals__JED1__Events__1=STOP_COMPLETED
```

Without `Events` every event is sent. The address must be HTTPS and reachable from the Yardsyncs server.
