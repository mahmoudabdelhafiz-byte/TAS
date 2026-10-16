# Load test: the peak booking hour

`peak.js` ([k6](https://k6.io)) plays the morning rush against a Yardsyncs server, four kinds of traffic at once:

| Who | What they do |
|---|---|
| Dispatchers | Sign in once; look at availability for the next five days; book an import pickup, mostly into 06:00–10:00; cancel 5 % and move 5 % of what they booked; think 0.5–1.5 s |
| Browsers | Refresh availability and the appointment list every 2–4 s |
| Trucks | Drivers on the way report their position from the app (every 5 s in CI, every 15 s like the real app) |
| Control room | Planners watch the control room and the gate board every 3 s |

A booking answered "slot just filled" (409) is a normal answer in a rush, not a failure. After the run,
`check.sql` checks that the booking rules held: no slot or import quota booked past its capacity, slot and category
counters equal to the appointments in them, no container in two live appointments.

## Running it

The server must run as **Development** (the test seeds companies and drivers through `/api/dev/load-seed` and reads
drivers' sign-in codes from the answers) with the sign-in rate limit lifted, because every virtual user comes from one
address. `compose.load.yml` does that on top of `deploy/`. Never point the test at a production server.

```bash
cd deploy
docker compose -f docker-compose.yml -f docker-compose.smoke.yml -f ../loadtest/compose.load.yml up -d db app
docker run --rm --network host -v "$PWD/../loadtest:/scripts" grafana/k6:0.54.0 run /scripts/peak.js            # CI size
docker run --rm --network host -e PROFILE=peak -v "$PWD/../loadtest:/scripts" grafana/k6:0.54.0 run /scripts/peak.js  # a busy terminal
docker compose -f docker-compose.yml -f docker-compose.smoke.yml exec -T db psql -U tas -d tas -At < ../loadtest/check.sql
```

`BASE_URL` points it at another server (a staging copy of the customer's installation). CI runs the `ci` profile in
the `deploy` job on every pull request; the timings are in the job's notices.

| Profile | Dispatchers | Browsers | Trucks reporting | Control room | Duration |
|---|---|---|---|---|---|
| `ci` | 60 | 20 | 150 (every 5 s) | 4 | 2 min |
| `peak` | 300 | 100 | 1 000 (every 15 s) | 10 | 10 min |

Pass marks (the test fails above them): no more than 1 % failed requests; 95 % of bookings under 1.5 s, of
availability, appointment lists and positions under 0.8 s, of the control room under 2 s and of the gate board under 1 s.

## Results

**2026-10-16, CI runner** (GitHub `ubuntu-latest`, 4 CPU / 16 GB; database, app and k6 on the same machine), profile `ci`:

| | p95 | p99 | max |
|---|---|---|---|
| Book | 29 ms | 607 ms | 823 ms |
| Availability | 6 ms | 610 ms | 1 058 ms |
| Appointment list | 11 ms | 203 ms | 507 ms |
| Position report | 13 ms | 1 058 ms | 1 132 ms |
| Control room | 15 ms | 1 166 ms | 1 193 ms |
| Gate board | 9 ms | 135 ms | 149 ms |

15 085 requests (115 per second), none failed. 1 838 trucks booked; the five days were full within the run, after which
dispatchers kept finding no free slot (5 245 times), as they do on a real rush morning. The slowest 1 % come in short
bursts on a machine that also runs the database and the load generator; the 95th percentiles are far under the marks.

**What the load test found and what changed**

- *The same container booked twice at the same moment got two appointments.* The "container already booked" check ran
  before the booking's transaction. Now each container is locked for the length of the transaction and checked again
  (`ConcurrencyApiTests`).
- *Connections.* The app's database pool defaulted to 100 connections, PostgreSQL's whole limit, leaving no room for
  backups and administrators. `deploy/` now sets the pool to 60 (`DB_POOL`) and PostgreSQL to 150 (`DB_MAX_CONNECTIONS`).
- Capacity counters held: no slot or quota was booked past its capacity, and the counters matched the appointments.

**Sizing.** One server of the size in `deploy/README.md` (4 CPU / 8 GB) carries a mid-size terminal's rush with room
to spare. Before go-live, run the `peak` profile against a staging copy of the customer's installation with the
customer's capacity templates and record the result here.
