# TAS — Progress Log

Last updated: 2026-10-15 (after notifications)

Status key: ✅ done · 🔜 next · ⬜ planned · ❓ needs a decision

---

## Done

### 1. Product definition (2026-10-13)
| | Item |
|---|---|
| ✅ | Scope for container + multipurpose terminals: TOS validation before booking, capacity slots, dual transactions, gate matching, multipurpose cargo mode |
| ✅ | Capacity model agreed: default capacity per hour per category → seasonal templates → exceptional overrides for specific hours/days |
| ✅ | Competitor review (Conroo): adopted driver-side features — simple multilingual app, traffic light, digital gate pass, geofence ETA, yard guidance, trucking company API |
| ✅ | Differentiators: lower cost, Arabic-first, FASAH/Mawani fit, multipurpose cargo |

### 2. Design & database (2026-10-13)
| | Item | Where |
|---|---|---|
| ✅ | Data model (33 tables) incl. driver app, geofencing, yard guidance, messaging | `docs/data-model.md` |
| ✅ | Database choice: PostgreSQL + PostGIS (SQL Server still possible for on-premise via EF Core) | `docs/data-model.md` |
| ✅ | Schema, validated on PostgreSQL 16 | `db/schema.sql` |

### 3. Capacity engine (2026-10-14) — commit `02a914f`
| | Item |
|---|---|
| ✅ | .NET 10 solution: Domain, Application, Infrastructure (EF Core + Npgsql), API |
| ✅ | Slot planning: default + seasonal templates, overrides (set/adjust/close), overbooking, quotas per category |
| ✅ | Availability: category quota vs slot total, quota release to pool, reservations (priority customers / walk-ins) |
| ✅ | Slot generation that keeps bookings and flags over-capacity instead of cancelling; background worker every 6 h |
| ✅ | Race-safe booking counters (10 concurrent bookings for 2 places → exactly 2 succeed) |
| ✅ | API: generate slots, availability per day/category, health |
| ✅ | GitHub Actions CI with PostgreSQL + PostGIS; test counts published per run |

### 4. Booking flow (2026-10-14) — commit `41e4e82`
| | Item |
|---|---|
| ✅ | Book a truck visit with 1–4 moves (dual transactions, twin 20') |
| ✅ | Multipurpose cargo booked by BL / cargo reference, trips drawn down from the release |
| ✅ | Checks before booking: ISO 6346 check digit, container not in two live appointments, company/truck/driver ownership, per-truck daily limit |
| ✅ | TOS adapter interface + configurable fake N4 (availability, customs/line/terminal holds, payment, booking, VGM, cut-off, cargo release) |
| ✅ | Failed check → booking rejected with reasons; warning → booked with amber light |
| ✅ | Cancel (cut-off 60 min before slot; terminal staff any time), reschedule (until gate-in), assign truck/driver |
| ✅ | Driver traffic light (red / amber / green / grey with reason) |
| ✅ | Automatic no-shows: capacity released, company no-show count increased; worker every 10 min |
| ✅ | Demo terminal seeder + demo containers for each scenario (Development only) |
| ✅ | Tests: 50 unit + 16 database integration, all passing in CI |

### 5. Authentication & roles (2026-10-14)
| | Item |
|---|---|
| ✅ | Login with email + password (PBKDF2 hashing), 30-min access tokens, 14-day refresh tokens |
| ✅ | Refresh tokens rotate on each use; re-using an old one signs the user out everywhere |
| ✅ | Lockout after 5 wrong passwords (15 min); login rate limit per IP; unknown emails indistinguishable from wrong passwords |
| ✅ | Logout; password change ends all other sessions; password policy |
| ✅ | Roles: CompanyAdmin, CompanyUser, Planner, GateClerk, TerminalAdmin, SysAdmin |
| ✅ | Company isolation: company users only see / book / change their own company's appointments |
| ✅ | Terminal scope: staff only at their own terminal; book on behalf of companies; planners/admins regenerate slots |
| ✅ | User creation within the admin's scope; first SysAdmin created from configuration |
| ✅ | API takes the user from the token (`userId` removed from request bodies) |
| ✅ | Database: auth columns, refresh token table, upgrade script `db/migrations/002_auth.sql` |
| ✅ | Tests: 79 unit + 16 database + 9 end-to-end API, all passing in CI |

### 6. Capacity admin API (2026-10-14)
| | Item |
|---|---|
| ✅ | Weekly default template and seasonal templates: create, edit (whole grid), remove seasonal |
| ✅ | Validation: overlapping windows, times (24:00 = midnight), quotas, capacity and overbooking limits, dates |
| ✅ | Overrides set / adjust / close for specific hours with a mandatory reason; lift an override |
| ✅ | Reservations for priority companies / walk-ins; quota release rules; terminal settings |
| ✅ | Slot board: capacity vs booked per slot and category, closed and over-capacity flags |
| ✅ | Audit log of every change: who, when, why, before / after values |
| ✅ | Affected slots regenerate immediately; bookings are kept and over-capacity is flagged |
| ✅ | Access: terminal staff view; planners and terminal admins change; companies have no access |
| ✅ | Tests: 94 unit + 16 database + 16 end-to-end API, all passing in CI |

### 7. Master data API (2026-10-14)
| | Item |
|---|---|
| ✅ | Companies: list / search, register, edit, activate / suspend with reason (suspended companies cannot book) |
| ✅ | Trucks: one plate form for Arabic and Latin input (Saudi letters mapped, Arabic-Indic digits), unique across companies |
| ✅ | Drivers: ID / Iqama, mobile normalized to +966…, preferred language |
| ✅ | Blocking trucks / drivers is a terminal decision (TerminalAdmin), with reason; blocked trucks can't be booked |
| ✅ | Transaction categories per terminal; new or re-activated categories are bookable at once (slots regenerate) |
| ✅ | Cargo releases (BL / DO) with trips / tonnage; trips can't drop below what's booked; companies see only their own |
| ✅ | Users: list within scope, edit, change role, deactivate (reason), reactivate, admin password reset |
| ✅ | Security: every request re-checks the account — deactivation and role changes take effect immediately |
| ✅ | All changes audited |
| ✅ | Tests: 121 unit + 16 database + 21 end-to-end API, all passing in CI |

### 8. Bulk booking (2026-10-14)
| | Item |
|---|---|
| ✅ | Upload `.xlsx` or `.csv` (comma / semicolon / tab, UTF-8, BOM); columns by name or alias in any order |
| ✅ | Dates and times as text, real Excel date/time cells, or raw Excel numbers |
| ✅ | Rows sharing a `visit` become one appointment (dual transactions, up to 4 moves) |
| ✅ | File checks: required columns, dates / times, one date-time-truck-driver per visit, container not twice in the file, 500-row limit |
| ✅ | Dry run (default): every booking check incl. TOS, plus capacity counted across earlier rows of the file; nothing booked |
| ✅ | Real run: visit-by-visit booking, partial success, per-row results with appointment numbers and reasons |
| ✅ | Trucks by plate (Arabic or Latin) and drivers by ID / Iqama, resolved for the company |
| ✅ | Downloadable Excel template (with instructions sheet) and CSV template |
| ✅ | Booking service split into prepare / commit so previews run exactly the same checks as bookings |
| ✅ | Tests: 129 unit + 16 database + 26 end-to-end API, all passing in CI |

### 9. Re-checks before arrival (2026-10-14)
| | Item |
|---|---|
| ✅ | Scheduled re-checks of appointments starting within 24 h (at most every 60 min each); an unreachable TOS leaves them due for the next run |
| ✅ | On-demand re-check by the company or terminal staff |
| ✅ | Start trip: re-checks right before the truck leaves, refuses while anything is red (hold, unpaid, window passed), marks EN_ROUTE; at most 12 h before the slot |
| ✅ | Item statuses, stored traffic light and last-checked time updated; changes written to the appointment history |
| ✅ | Notification outbox: STATUS_RED / STATUS_CLEARED queued for the driver (SMS, driver's language) and the booker (email) |
| ✅ | Booking and re-checks share one set of item checks (check digit + TOS) |
| ✅ | Database: `appointment.last_validated_at`, upgrade script `db/migrations/003_revalidation.sql` |
| ✅ | Tests: 129 unit + 20 database + 27 end-to-end API, all passing in CI |

### 10. Gate (2026-10-14)
| | Item |
|---|---|
| ✅ | Generic lane API for Autogate / OCR / clerk screens: plate, container, QR and appointment-number reads |
| ✅ | Matching: QR / number first, then plate (Arabic or Latin) nearest to now, then container; warnings for reads that don't fit |
| ✅ | Re-check against the TOS at the gate; admit or refer (early, late, not cleared, blocked, already in, cancelled, no appointment) |
| ✅ | Trouble desk: admit with note, reject with note (capacity released), walk-in booking into the current slot using the walk-in reserve, dismiss |
| ✅ | Statuses ARRIVED → GATE_IN → COMPLETED / REJECTED_AT_GATE; check-in method recorded (OCR, QR, clerk) |
| ✅ | Gate event log; turn time (arrival → exit) and time inside (gate-in → exit) |
| ✅ | Gate board: trucks at the gate and inside with minutes, open troubles, completed today, average turn time |
| ✅ | Lanes per terminal (demo: IN-1, IN-2, OUT-1) |
| ✅ | Database: `gate_trouble`, upgrade script `db/migrations/004_gate.sql` |
| ✅ | Tests: 139 unit + 26 database + 28 end-to-end API, all passing in CI |

### 11. Navis N4 adapter (2026-10-14)
| | Item |
|---|---|
| ✅ | Read-only N4 checks via ArgoService `genericInvoke` (SOAP, basic auth, per-terminal scope and timeout) |
| ✅ | `TasLookup` Groovy code extension template returning units, bookings / EDOs and BLs as JSON (`integrations/n4/`) — lines marked VERIFY to confirm on the customer's N4 version |
| ✅ | Rules: import in yard (transit state), category, holds per type, booking left, cargo cut-off, VGM warning, empty release, empty return, BL release |
| ✅ | Hold mapping: configurable N4 hold id → check, keyword fallback (customs / line / payment / terminal) |
| ✅ | Multi-terminal: N4 settings per TAS terminal code; `Tos:Adapter` = Fake or N4 |
| ✅ | N4 outage: booking warns (or rejects, configurable) and is re-checked when N4 answers; trip start refused; gate refers to trouble desk |
| ✅ | Tests: evaluator unit tests, SOAP client against a fake server, booking / re-check / gate flows with a stub N4 |

### 12. Notifications (2026-10-15)
| | Item |
|---|---|
| ✅ | Outbox delivery worker: claims due messages (safe with several API instances), renders, sends, records recipient / provider id / errors |
| ✅ | Messages in Arabic, English, Urdu, Hindi, branded Yardsyncs: on hold, cleared, reminder before the slot (with gate-opening time), slot closed by the terminal |
| ✅ | Drivers by SMS or WhatsApp (configurable); dispatchers by email; reminders to drivers only, once |
| ✅ | Slot closed: closing booked hours (or a category) notifies affected trucks not yet at the gate, with the planner's reason |
| ✅ | Providers: Unifonic (SMS), Twilio (SMS, WhatsApp), SMTP (email); "Log" mode until configured |
| ✅ | Retries after 1 / 5 / 15 / 60 min; permanent errors fail at once; expiry after 12 h; reminders dropped if the appointment is no longer upcoming |
| ✅ | Endpoint: messages per appointment for the help desk |
| ✅ | Database: delivery columns on `notification_log`, upgrade script `db/migrations/005_notifications.sql` |
| ✅ | Tests: templates in every language, providers against a fake server, delivery / retry / expiry / reminder / slot-closed flows |

### 13. Web portal — round 1 (2026-10-15)
| | Item |
|---|---|
| ✅ | `web/`: React + TypeScript (Vite), served by the API from `wwwroot` after `npm run build`; hash routes, no extra router |
| ✅ | Sign-in with silent token refresh; menus by role (companies: book, appointments; planners: slot board, appointments, book, gate; gate clerks: gate, appointments) |
| ✅ | Arabic / English with full right-to-left layout; times always in the terminal's time zone; Gregorian dates and Latin digits in Arabic |
| ✅ | Book a truck: move type, day, arrival hours with free places, up to 4 moves (container / empty by booking / cargo by BL), truck and driver; refusals show the failed checks |
| ✅ | Appointments: day list with status and traffic light; details with checks and messages sent; re-check, truck is leaving, move to another hour, change truck/driver, cancel with reason |
| ✅ | Slot board: each hour drawn as a bay filling by move type against its capacity line; closed and over-capacity hours marked; close / set / add-remove for one hour with a reason; remove changes |
| ✅ | Gate: live board (at the gate, inside, completed, average turn time), truck check by appointment number or plate, gate in / gate out, trouble desk (let in, turn away, close case) |
| ✅ | API: `GET /api/terminals` (terminal picker) |
| ✅ | Tests: time zone, Arabic texts complete, routes and role menus, token refresh and error handling (`npm test`); CI builds and tests the portal |

### 14. Book by BL or several containers (2026-10-15)
| | Item |
|---|---|
| ✅ | Booking screen: "One truck" or "Several trucks"; find containers by import bill of lading or a pasted list (from Excel, one per line or comma-separated) |
| ✅ | Each container checked before booking (holds, release, payment, check digit, already booked); ready ones pre-selected, blocked ones shown with the reason |
| ✅ | Two 20′ containers per truck (optional), 40′/45′ alone; trucks spread over one or more chosen hours by free places; one appointment per truck; results per truck, retry the ones not booked |
| ✅ | API: `POST /api/terminals/{id}/containers/lookup`; TOS adapter lists containers of a BL (fake TOS config, N4 `blUnits` in TasLookup — VERIFY on the customer's N4) |
| ✅ | Tests: lookup API (BL, pasted list, other company's booking hidden), N4 answer parsing, ISO sizes, truck grouping and hour allocation |

### 15. Web portal — round 2, part 1 (2026-10-15)
| | Item |
|---|---|
| ✅ | Capacity setup — weekly plans: the week as a grid (day × slot) coloured by capacity; drag to select hours, set trucks per slot, overbooking and a limit per move type, or close hours; saved as plan windows, slots regenerated and over-capacity reported |
| ✅ | Seasonal plans (dates, priority) started as a copy of the default plan; remove a seasonal plan |
| ✅ | Reserved places for a company or the walk-in reserve, with release before the slot; release rules per move type; booking rules (early / late grace, cancel cut-off, appointments per truck per day) |
| ✅ | Bulk upload screen: template download, check the file (nothing booked), book the visits that passed, result per visit |
| ✅ | Tests: plan grid ↔ windows, selection, local time → instant in the terminal's zone |

### 16. Web portal — round 2, part 2 (2026-10-15)
| | Item |
|---|---|
| ✅ | KPI dashboard for terminal staff: slot use, appointments, no-shows, on-time arrivals, turn and gate waiting time, walk-ins; slot use by day and by hour of the day, turn time by day; table view; last 7 / 30 days, next 7 days or any range |
| ✅ | API: `GET /api/terminals/{id}/kpis?from&to` (up to 92 days) |
| ✅ | Fleet & users: trucks and drivers (add; block / unblock by terminal admins), users (add with a first password, activate / deactivate), companies for terminal admins (register, suspend, activate) |
| ✅ | Tests: KPI calculation (rates, on-time window, turn and wait times, closed slots), KPI API access and range |

### 17. Driver app — part 1 (2026-10-15)
| | Item |
|---|---|
| ✅ | API: driver sign-in with a 6-digit code by SMS (hashed, 5 minutes, 5 tries, 3 codes per 15 minutes, same answer for unknown numbers); session bound to one phone; own token audience |
| ✅ | API: my appointments across companies (by mobile number), one appointment, start trip, sign out; terminal time offset for display |
| ✅ | Database: `driver_otp` (migration `006_driver_login.sql`); driver devices and verifications in use |
| ✅ | Flutter app `mobile/`: sign in, my appointments (traffic light, terminal time, containers), appointment with reason, gate QR code and "I am leaving now"; Arabic / English / Urdu / Hindi with right-to-left |
| ✅ | CI job: analyze, tests, debug APK as a download for testers |
| ✅ | Tests: sign-in flow, limits, one phone at a time, blocked drivers, token separation (API); texts, parsing, terminal time, sign-in screen (app) |

### 18. Gate pass and on the way (2026-10-15)
| | Item |
|---|---|
| ✅ | Rotating gate pass: per-driver secret (encrypted at rest), QR code changing every 30 s, made on the phone without mobile data; checked at the lane and matched to the appointment; revoked when the driver signs in on another phone |
| ✅ | Positions from the app while on the way (only then): arrival estimate (distance to the gate × road factor ÷ speed), terminal areas entered, automatic check-in in the pre-gate area |
| ✅ | Terminal areas API (PostGIS): approach, parking, pre-gate, gate, yard zones…; demo areas for the demo terminal |
| ✅ | Arrival estimate shown in the portal's appointment list and in the app |
| ✅ | Tests: shared gate-code vectors in C# and Dart, gate pass at the lane, positions and check-in, areas |

### 19. Yard navigation (2026-10-15)
| | Item |
|---|---|
| ✅ | Container's yard position from the TOS at gate-in: N4 `TasLookup` returns block and slot (row.bay.tier); the demo TOS gives listed, receival-block or made-up positions |
| ✅ | Terminal road map (`terminal_map`): roads as GeoJSON lines with one-way flags; terminal admins upload it (`PUT /api/terminals/{id}/yard-map/roads`); demo roads for the demo terminal |
| ✅ | Routes on the terminal's own roads (shortest path that respects one-way roads), from the truck's position or the gate to each block, then to the exit |
| ✅ | "Where to go" for the driver after gate-in: drops before pickups, one stop per block, then exit; saved as `yard_instruction` rows; a container moved since the last look is flagged; if the TOS is down the last known stops are shown, marked as not live |
| ✅ | App: "Where to go" button after gate-in; next stop in large text (block, row, bay, tier) and a map drawn on the phone from the terminal plan — no map service, works with weak signal; refreshes every 30 s |
| ✅ | Tests: routing (one-way, unreachable), stop order and route ends, moved container, road upload rights and validation, app parsing and map projection |

### 20. Terminal map editor (2026-10-15)
| | Item |
|---|---|
| ✅ | Portal screen "Terminal map" for terminal staff: terminal admins edit, planners view |
| ✅ | Draw areas (yard blocks with their TOS block name, gate, exit, pre-gate, parking, CFS, approach) and roads; reshape by dragging corners, add / remove corners; one-way roads with arrows and reverse |
| ✅ | New roads join existing ones where they start or end on them; moving a junction keeps the roads joined |
| ✅ | Check before saving: gate and exit drawn, roads in one piece, every block reachable from the gate and left to the exit, block names present and unique |
| ✅ | Try a route between any two points on the current (unsaved) roads, same routing rules as the server |
| ✅ | Import a GeoJSON export from CAD / GIS (polygons → areas, lines → roads); background map tiles configurable per installation, plain grid without internet |
| ✅ | API: change an area (`PUT /geofences/{id}`), read the saved roads; yard blocks need a unique TOS block name |
| ✅ | Tests: routing, map check, snapping, projection and import (web); area edits and roads read-back (API) |

### 21. Background tracking and spoken directions (2026-10-15)
| | Item |
|---|---|
| ✅ | Position on the way keeps going with the phone locked or another app open: Android foreground service with a visible "sharing your location" notification, iOS background location with the blue indicator |
| ✅ | Starts when the trip starts (or when the app opens with a trip on the way), stops by itself at the pre-gate check-in, when the trip ends or on sign-out; sends every minute, sooner after 500 m, never more than every 15 s |
| ✅ | Spoken directions in the yard in the driver's language: block, row / bay / tier and distance; said again when the next stop comes or the container moves; repeat and mute buttons (mute remembered) |
| ✅ | Urdu: phones without an Urdu voice speak the Hindi text with the Hindi voice; no voice at all → stays silent rather than reading in a wrong voice |
| ✅ | While the truck moves (over ~9 km/h) the "Done here" button waits: "Stop the truck to confirm" |
| ✅ | Tests: spoken texts in four languages, distance rounding, position sending rules |

### 22. Yardsyncs mobile: iPhone, push, in-gate ticket (2026-10-15)
| | Item |
|---|---|
| ✅ | App name **Yardsyncs mobile**, `com.yardsyncs.mobile` on Android and iPhone; CI builds both (iPhone unsigned) |
| ✅ | Push through Firebase (Android and iPhone): drivers with the app get push instead of SMS; SMS when a push can't be delivered; Firebase set per installation |
| ✅ | At gate-in the TOS yard position becomes the in-gate ticket: push "go to B2 (14.03.2)", ticket in the app (appointment, truck, lane, time, stops) |

### 23. Arrival at the container and events for the terminal's systems (2026-10-15)
| | Item |
|---|---|
| ✅ | Phone used up to the gate (gate pass) and inside the terminal for directions; position shared on the way and inside, not while waiting at the gate |
| ✅ | Reaching the block of the current stop (within 10 m, slow) marks it arrived; the driver hears and sees it |
| ✅ | Events for the terminal's systems: TRUCK_CHECKED_IN, TRUCK_AT_BLOCK, STOP_COMPLETED — kept in an outbox, sent to the terminal's webhook (signed, retried) when it subscribes; visible through the API |
| ✅ | Tests: arrival detection, stop confirmation, webhook delivery / signature / retry / skip |

### 24. Control room (2026-10-16)
| | Item |
|---|---|
| ✅ | Portal screen for terminal staff (planners, admins, gate clerks): the yard map with every truck inside at its last position, its target block outlined, and a list of trucks inside / at the gate / on the way |
| ✅ | Each truck's stage (driving to the block, at the block, leaving), current stop with block and row.bay.tier, time inside; flagged when inside longer than the terminal's target (`turn_target_min`, default 60) or when the app stopped sending positions |
| ✅ | Today: visits done, average time inside, gate to block, at the block; truck detail with every stop and its times |
| ✅ | The app's last position is kept on the appointment (on the way and inside only; migration 009) |
| ✅ | Tests: board from the gate to the block and out, turn-time target setting |

### 25. Yard times in the KPIs, control-room alerts (2026-10-16)
| | Item |
|---|---|
| ✅ | KPI screen "Inside the terminal": gate to block, at the block, block to gate-out, share of visits the app followed; time at the block by day; by block, slowest first (drive there, time there) |
| ✅ | Control room: a bar listing trucks that need attention (inside longer than the target, app gone quiet), each opening the truck |
| ✅ | Tests: yard KPI calculation, report through the API |

### 26. Password reset and invitations (2026-10-16)
| | Item |
|---|---|
| ✅ | "Forgot your password?" on the sign-in page: a link by email, works once for 60 minutes; the same answer for unknown emails; 3 per account per hour |
| ✅ | New users are invited by email and choose their own password (link valid 7 days); administrators can still set a first password, and can send a fresh link to any user they manage |
| ✅ | Setting a password from a link ends every session of the account and lifts a lock-out; only a hash of the link is stored (migration 010) |
| ✅ | Emails in Arabic or English; link address from `PasswordLinks:PortalUrl` |
| ✅ | Tests: reset flow, link reuse, limits, invitation and resend |

### 27. Track containers (2026-10-16)
| | Item |
|---|---|
| ✅ | Portal screen "Track containers" for trucking companies and terminal staff: by bill of lading or container numbers, without booking |
| ✅ | Each container: ready to collect, not in the yard yet, on hold (with the reason), booked (own appointment; another company's only "booked"), collected; and each check — in yard, customs, line release, terminal hold, charges |
| ✅ | Book the ready ones in one click (the booking screen opens with them filled in) |
| ✅ | Follow containers: re-checked every 15 minutes, an email the moment one becomes ready to collect (Arabic, English, Urdu, Hindi); 30 days or until collected (migration 011) |
| ✅ | Tests: states from a bill of lading, visibility of other companies' bookings, following and the ready email |

### 28. Production readiness, part 1 (2026-10-16)
| | Item |
|---|---|
| ✅ | One Docker image with the API and the portal; non-root, JSON logs, version stamped |
| ✅ | Database created and upgraded by the app from the SQL in `db/` (version history in `schema_migration`, lock against parallel upgrades, `migrate` command); CI checks on every pull request that the previous schema plus new migrations equals `schema.sql`, and that migrations run twice safely |
| ✅ | On one server (`deploy/`): PostgreSQL + PostGIS, the app, HTTPS with Caddy (Let's Encrypt or internal certificate authority), nightly backups with retention; guide for install, upgrade, restore, cloud hosting and monitoring |
| ✅ | Health `/health/live` and `/health/ready`; `/api/ops/status` (version, database, message and event backlogs) for admins |
| ✅ | Security headers (CSP, no framing, nosniff), HSTS, client address and https from the reverse proxy (rate limits per real client) |
| ✅ | CI builds the image and starts it with a fresh database: schema created, ready, portal served, admin signs in, restart, backup |

### 29. Security and personal data review (2026-10-16)
Findings and the PDPL picture in [`security.md`](security.md).

| | Item |
|---|---|
| ✅ | Password links built only from `PasswordLinks:PortalUrl` on a real server (no forged `Host` header in reset emails) |
| ✅ | Sign-in codes and password links kept out of the logs when no provider is configured; recipients masked |
| ✅ | Driver codes and portal passwords: tries counted atomically (no parallel guessing past the limit); a code signs in once; 10 codes per number per day |
| ✅ | App refuses to start with test switches on outside Development / Testing; `/api/ops/status` lists settings to look at |
| ✅ | Bulk upload: zip-bomb workbooks refused, rows / columns read capped, request size capped |
| ✅ | Daily clean-up of personal data (positions, message addresses, events, codes, sessions, links, old push addresses), periods per installation |
| ✅ | Drivers: copy of data and erasure (portal and API), audited, refused while a visit is under way |
| ✅ | Yardsyncs mobile: location notice before the first trip (what, why, until when, rights), in the four languages |


### 30. Load test (2026-10-16)
Details and results in [`loadtest/README.md`](../loadtest/README.md).

| | Item |
|---|---|
| ✅ | k6 test of the morning rush: dispatchers booking the busy hours (with cancels and moves), people refreshing lists, trucks reporting positions, control room and gate board; profiles `ci` and `peak` |
| ✅ | Runs in CI on every pull request against the Docker image with a fresh database; fails on > 1 % errors or slow 95th percentiles; then checks the booking rules in the database |
| ✅ | Found and fixed: the same container booked twice at the same moment got two appointments (lock per container in the booking transaction) |
| ✅ | Found and fixed: the database pool could take every PostgreSQL connection (pool 60, PostgreSQL 150 in `deploy/`) |
| ✅ | Found and fixed (later run): moving two appointments in opposite directions at once could deadlock; both hours are now locked in a fixed order |


### 31. Releases and environments (2026-10-16)
Details in [`releasing.md`](releasing.md).

| | Item |
|---|---|
| ✅ | Image published to GitHub's registry on every merge (`edge`, `<VERSION>-main.<n>`) and on every tag (`1.2.0`, `1.2`, `latest`), with provenance and a software bill of materials |
| ✅ | Release page per version: install kit, image as a file for terminals without internet, checksums |
| ✅ | `deploy/upgrade.sh`: backup, switch version, wait until ready; `--rollback` to the version and database before; tested in CI on every pull request |
| ✅ | Environments: staging upgraded on every merge, production on a release after approval — over SSH once the servers and secrets exist |
| ✅ | Known vulnerabilities: image scanned (Grype), .NET and npm packages checked, Dependabot weekly |


### 32. User guides (2026-10-16)
| | Item |
|---|---|
| ✅ | **Help** in the portal for every role, in Arabic and English, printable: getting started, booking and appointments, tracking containers, fleet and users (with drivers' data rights), Yardsyncs mobile for drivers (to print and hand out), the gate, the control room, capacity planning, terminal administration with onboarding a trucking company |
| ✅ | Guides are Markdown in `web/src/help/{en,ar}/`; tests check both languages have the same sections and that every button or label the guides name in bold is a word on the screen (portal and app), so the guides can't drift from the screens unnoticed |
| ✅ | Yardsyncs mobile: the Arabic, Urdu and Hindi location texts said sharing stops at the pre-gate; now they say what the app does (on the way and inside, not while waiting at the gate, not after leaving) |


### 33. Public documentation and AI assistant (2026-10-16)
| | Item |
|---|---|
| ✅ | Public documentation at `#/docs` on every installation, no sign-in: all user guides (Arabic / English) and technical pages for IT teams and integrators — system overview, API, integration events (payload, signature, retries), installing and running, security and personal data, the AI assistant; linked from the sign-in page and from Help; printable |
| ✅ | AI assistant in the portal (Anthropic Claude, `claude-sonnet-5-5` by default): chat panel on every page, Arabic / English, suggestions per role; answers from the guides (technical pages too for terminal staff) and read-only look-ups with the user's own permissions; never changes anything; off until an API key is set; live data can be switched off |
| ✅ | Limits and cost: 60 questions per user per day, 10 per minute, at most 5 look-ups per question; no conversation text stored; today's questions and tokens in `/api/ops/status`; prompt caching for the guides |
| ✅ | Tests with a scripted model: guides and context in the prompt, look-ups scoped to the user's company, limits, provider errors, the look-up cap; Anthropic client against a fake server |


### 34. Settings per terminal, part 1 (2026-10-16)
| | Item |
|---|---|
| ✅ | TOS adapter per terminal (`Terminals:<code>:Tos`: N4 or Fake), default `Tos:Adapter`; checks record which TOS answered; unknown names stop the server from starting |
| ✅ | Message providers and accounts per terminal (`Terminals:<code>:Notifications:…` over `Notifications:…`): SMS / WhatsApp / email / push provider, SMS sender name, email sender; each message remembers its terminal (migration 013) |
| ✅ | The name messages are signed with per terminal (`Terminals:<code>:Brand`, default `Brand`, then Yardsyncs); password emails for terminal staff use their terminal's |
| ✅ | `/api/ops/status` lists every terminal's set-up (TOS, name, providers, sender names, webhook) |

### 35. Operators (2026-10-16)
| | Item |
|---|---|
| ✅ | Terminal operators (migration 014): an operator groups the terminals one company runs on the installation; a terminal is in one operator or none |
| ✅ | New role **Operator admin**: a terminal admin at every terminal of the operator (capacity, gate, map, categories, staff, companies), nothing outside it. The operator's terminal list is read on every request, so moving a terminal or stopping the operator counts at once |
| ✅ | System admins create operators, move terminals in and out, add operator admins (`/api/operators`, `PUT /api/terminals/{id}/operator`); operator admins add fellow admins and staff for the operator's terminals; all changes in the audit log |
| ✅ | Portal: **Operators** tab under Fleet & users; operator admins pick among the operator's terminals; `/api/ops/status` shows an admin only their own terminals' set-up |
| ✅ | Competing operators stay on separate installations (or separate cloud instances); operators group one company's terminals |

### 36. Country packs (2026-10-16)
| | Item |
|---|---|
| ✅ | Country packs for Saudi Arabia, the UAE, Oman, Bahrain, Kuwait and Qatar: local mobile number forms, Arabic plate letters, the drivers' ID document, currency, time zone, public holidays |
| ✅ | `Country` per installation (mobiles and plates: companies and drivers are shared) and `Terminals:<code>:Country` per terminal (holidays, currency); unknown codes stop the server from starting |
| ✅ | Public holidays: fixed days and lunar ones from the Umm al-Qura calendar (expected dates); `GET /api/terminals/{id}/holidays?year=`; the slot board names the holiday and offers **Close the whole day** (nothing closes by itself) |
| ✅ | `GET /api/country` (public): the portal's driver form shows the country's ID document and mobile example; the drivers' app shows the example before sign-in |

### 37. Language packs (2026-10-16)
| | Item |
|---|---|
| ✅ | A language pack is one JSON file per language (`deploy/languages/<code>.json`, mounted into the app) with any of three parts: portal, drivers' app, messages (notifications, sign-in code, password emails) |
| ✅ | New languages, or corrections to the built-in ones (e.g. a native speaker's review of Arabic, Urdu or Hindi) without a new version; whatever a pack leaves out shows in English |
| ✅ | Checked at start: code matches the file name, known messages only, only each message's own `{placeholders}`; a mistake stops the start with the reason |
| ✅ | `GET /api/languages` and `GET /api/languages/{code}` (public); the portal's language link becomes a list with a third language; the app adds pack languages to its menu; drivers' and users' languages are checked against the installation's |
| ✅ | `template.json` for translators with every English text; `npm run language-template` keeps it current (checked by tests on both sides) |

### 38. Branding (2026-10-16)
| | Item |
|---|---|
| ✅ | Name, main colour and logo for the installation and each operator (migration 015); an operator's shows for its terminals, else the installation's, else Yardsyncs |
| ✅ | An operator's own web address: sign-in there shows its look from the first page |
| ✅ | Portal: colours, logo and name everywhere (texts that name Yardsyncs name the brand; the product documentation stays Yardsyncs); **Look and name** under Fleet & users → Operators |
| ✅ | Drivers' app: colour, logo and name from the server; messages signed with the terminal's brand (a terminal's own Brand setting still first) |
| ✅ | Colours must carry white text (4.5:1); logos PNG / JPEG / WebP up to 256 KB, checked by their bytes, served with nosniff and a long cache |

### 39. TOS adapter kit (2026-10-16)
| | Item |
|---|---|
| ✅ | The rules every adapter's answers keep, as code (`TosContract`): which checks each kind of move must get, reasons on every warning and failure, answers only for the items asked, clean BL lists and yard positions, `TosUnavailableException` when the TOS can't answer |
| ✅ | Every live answer is held to them (`TosContractGuard`): a breach counts as "TOS unreachable" (warning and re-check, never a pass) and is logged as an error |
| ✅ | Conformance tests any adapter must pass (`TosAdapterConformance` with `TosScenario`); the demo TOS and N4 (against a simulated TasLookup) pass them |
| ✅ | "Connecting another TOS" in the public documentation; the demo TOS can act unreachable (`FakeTos:Unreachable`) to try the warning flow |

### 40. Terminal onboarding (2026-10-16)
| | Item |
|---|---|
| ✅ | Add a terminal (`POST /api/terminals`): system admins, or operator admins for their own operator; starts with the usual move types (breakbulk too for multipurpose), an in and an out lane and the usual booking rules |
| ✅ | The checklist (`GET /api/terminals/{id}/setup`): move types, weekly plan, bookable slots, gate lanes, TOS, staff, companies, map; "ready to book" once the first four are done |
| ✅ | **Terminal set-up** screen: the steps in order, each with its quick actions (usual move types, a starter weekly plan, make slots, add lanes and move types, test the TOS connection) or a link to the screen that does it |

### 41. Settings per terminal, part 2 (2026-10-16)
| | Item |
|---|---|
| ✅ | Driver message channel per terminal (`Terminals:<code>:Notifications:DriverChannel`: SMS or WhatsApp) for drivers without the app, including push fall-back and reminders |
| ✅ | Gate identification per terminal: the gate pass always; appointment number, plate and container number can each be turned off (**At the gate** in Capacity setup → Rules) |
| ✅ | Driver identity per terminal: not checked, a named driver, or the named driver's own gate pass; trucks that don't meet it go to the trouble desk as "Driver not identified" (migration 016) |

### 42. Fees (2026-10-16)
| | Item |
|---|---|
| ✅ | Per terminal, in its country's currency: booking, no-show and late-cancellation fees, and the late-cancellation window (Capacity setup → Rules → Fees, terminal admins) |
| ✅ | Charges recorded against the appointment, once per kind (migration 017): at booking, at no-show, at a late cancellation; cancelling in time, or a cancellation by the terminal, waives the booking fee |
| ✅ | Companies see the fees before booking; **Charges** screen and `GET /api/terminals/{id}/charges` (JSON or CSV) with totals per company; companies see their own only |
| ✅ | Terminal admins waive a charge with a reason (kept, audited); billing and payment stay in the terminal's own system |

### 43. The name Yardsyncs in the code (2026-10-16)
| | Item |
|---|---|
| ✅ | Projects, namespaces and the solution: `Yardsyncs.Api`, `Yardsyncs.Application`, `Yardsyncs.Domain`, `Yardsyncs.Infrastructure`, `Yardsyncs.slnx`; the app runs as `Yardsyncs.Api.dll` |
| ✅ | `ConnectionStrings__Yardsyncs` (`ConnectionStrings__Tas` from before still works); bulk booking templates named yardsyncs-… |
| ✅ | Kept on purpose, so installed systems need no change: the database and its user (`tas`), backup file names, the N4 code extension `TasLookup`, the sign-in token issuer, and the `TAS_DOMAIN` setting |

### 44. Autogate lane model (2026-10-17)
| | Item |
|---|---|
| ✅ | Lanes have a state — open, closed, out of order — set by the gate staff with a reason (migration 018); trucks checked at a lane that isn't open are refused with that reason; the gate screen shows every lane and opens / closes them |
| ✅ | Devices per lane, vendor-neutral: camera, OCR, RFID reader, kiosk, barrier, traffic light, display, printer, intercom, weighbridge — with what each reads, vendor, model and address (`gate_lane_device`) |
| ✅ | Way of working per lane: manual or automatic. Automatic needs a barrier and a way to identify trucks without a clerk; the lane shows how it identifies trucks (its devices + the terminal's gate rules) and what it is missing |
| ✅ | **Lanes** screen for terminal admins; every change audited; the demo terminal's IN-1 and OUT-1 come equipped |

### 45. Autogate device adapters (2026-10-17)
| | Item |
|---|---|
| ✅ | Device API `/api/devices/…`, vendor-neutral, pull only (nothing reaches into the terminal's network): each device signs in with its own key (`X-Device-Key`, issued once by a terminal admin, only the fingerprint stored), reports status, sends reads, fetches and confirms commands (migration 019) |
| ✅ | Reads in: plate with confidence, up to 4 containers with ISO code (check digit tested, misreads flagged), seals, damage, photo references, RFID tag, kiosk entry or gate pass, weight — each kind sends only what it produces; normalized like the gate matches; kept for the events privacy period |
| ✅ | Commands out: barrier open / close, light red / green / off, display show / clear, ticket print; barrier and light commands expire after 30 s and are never handed out late; each confirmed done or failed |
| ✅ | Portal: device connection (online / fault / offline / not connected) and **Device key** on the Lanes screen; latest read, devices down and **Open barrier** on the gate screen; lane activity endpoint |
| ✅ | Technical page *Connecting lane devices*; reference adapter `deploy/device-adapter/simulator.mjs` |

### 46. Autogate lane controller (2026-10-17)
| | Item |
|---|---|
| ✅ | One transaction per truck at an automatic lane (migration 020): reads gathered (same truck within 30 s unless another plate), decided after 3 s of quiet or at once for a kiosk entry / gate pass, by the same rules as a clerk (match, TOS re-check, window, holds, blocks) |
| ✅ | Let in: green, barrier open, welcome on the display, gate ticket at the printer; the barrier's confirmation gates the truck in. Referred: red, the reason on the display, a trouble-desk case; letting it in there opens that lane's barrier; a barrier that fails calls a clerk |
| ✅ | Exit lanes: gate-out by plate or appointment number, then green and open, or a clerk |
| ✅ | Gate screen shows each automatic lane's current truck; `GET …/gate/transactions`; settings `LaneController__*`; runs inside Yardsyncs (next to the lanes when installed at the terminal) |

### 47. Remote clerk station (2026-10-17)
| | Item |
|---|---|
| ✅ | **Remote clerk** screen (gate clerks, planners, admins): lane tiles, trucks waiting for a clerk oldest first, and per truck its reads with photos, misread containers flagged, the commands and the case |
| ✅ | Correct the plate / containers or type the appointment number, then **Check again**: the old case closes, the lane decides anew (barrier opens or a fresh case); corrections recorded per transaction for the OCR read rate (migration 021) |
| ✅ | Let in / turn away with a note (barrier opens / display tells the driver); intercom `Call` / `Hangup` commands; message on the lane display; open barrier |

### 48. Gate passages in the TOS, gate ticket (2026-10-17)
| | Item |
|---|---|
| ✅ | Per terminal (`tos_gate_write`, off by default): each gate-in (truck visit + one transaction per move, with seals seen at the gate) and gate-out is queued with the passage (migration 022) and sent by a worker; retried while the TOS is unreachable, refused ones wait for a person; gate-out only after its gate-in |
| ✅ | `ITosGateWriter` for adapters; demo TOS records them; N4 through its gate web service (`process-truck` at in / out stage, transaction types per move type) — **to verify on the customer's N4 test system** |
| ✅ | Lanes screen: gate records not yet in the TOS, send now / send again; settings switch under Capacity setup → Gate |
| ✅ | Gate ticket (equipment interchange receipt): company, truck, driver, times and lanes, moves with seals, damage seen at the gate, TOS visit; printable from the gate screen |

### 49. Inspection at the gate (2026-10-17)
| | Item |
|---|---|
| ✅ | Companies declare a container's seal on the appointment until it reaches the gate; the gate checks the seal seen against it (in order / different number / broken / missing) (migration 023) |
| ✅ | Inspection per container and passage: seal, damage (side, kind, severity, note, photo) and photos taken at the gate (stored in the database, at most 3 MB, eight per inspection); automatic lanes record what the cameras read, clerks confirm or correct; damage new at gate-out is shown |
| ✅ | **Inspections** screen and the appointment panel: companies see their own and dispute; planners and admins accept or reject with a note (audited); the gate ticket and the TOS gate records carry the inspected seals and damage |

### 50. Exit gate (2026-10-17)
| | Item |
|---|---|
| ✅ | Each move type says which way its cargo goes (in / out; guessed from the code, set per move type in terminal set-up) (migration 024) |
| ✅ | At gate-out the containers seen on the truck (exit lane cameras, grouped around the truck's plate) are checked against the visit: what it came to collect is on it, what it brought is not, nothing else; lanes without a container camera are not checked |
| ✅ | A problem holds the truck: the clerk sees why and lets it out with a note; automatic exit lanes hold it on red and the remote clerk lets it out; every exit recorded with what was seen, the problems, who let it out, and the truck's weight in and out (actual tonnage of a cargo move) |
| ✅ | Gate ticket shows the exit check and the cargo weight; turn time as before |

### 51. Two modules: truck appointment system and Autogate (2026-10-17)
| | Item |
|---|---|
| ✅ | Autogate is its own project (`src/Yardsyncs.Autogate`): devices, automatic lanes, lane controller, remote clerk, inspection, gate tickets, the exit check and TOS gate records. The truck appointment system never references it; it plugs into the gate through extension points (gate passages, exit check, lane equipment) |
| ✅ | Installed or not per installation (`Modules__Autogate`, default on); per terminal, a system admin can switch it off (setting `module_autogate`), so one installation serves terminals with and without it. Without it: its API answers 404, its screens leave the menu, lanes can't be automatic, trucks leave unchecked, no TOS gate records |
| ✅ | The terminal's own gate system on the gate API: keys made and revoked by terminal admins (**Lanes → Gate systems**), each acting as a gate clerk of that terminal that can't sign in (`X-Api-Key`, migration 025); `POST …/gate/arrivals/check` answers what the gate would decide without recording anything |
| ✅ | **Lanes** stays in the truck appointment system (lane codes for the terminal's own gate system); docs page "Connecting your own gate system"; `GET /api/modules?terminalId=` |

### 52. Lane KPIs (2026-10-17)
| | Item |
|---|---|
| ✅ | Per lane and in total, from the lane controller's transactions: time at the lane (median and 9 in 10, first read to through the barrier), time to the decision, trucks without a clerk, exceptions and why clerks helped, and abandoned reads (`GET /api/terminals/{id}/gate/lane-kpis?from=&to=`, Autogate only) |
| ✅ | OCR read rate: plates read and not corrected by a clerk at lanes with a plate camera; containers the same, for trucks with containers |
| ✅ | **KPIs** screen: a **Lanes** section with tiles, exceptions by day, the reasons, and one row per lane |

### 53. Gate pass at the kiosk, yard position at gate-in (2026-10-17)
| | Item |
|---|---|
| ✅ | The driver shows the app's rotating gate pass at the kiosk of an automatic lane: decided at once, as at a clerk's lane (tested end to end: driver sign-in, pass, kiosk scan, barrier, gate-in) |
| ✅ | The printed gate ticket now comes when the barrier has opened (the truck is in), with the TOS's yard position ("Go to: B2 (14.03.2)"); the lane display shows where to go; the driver app gets the stops by push as before |
| ✅ | The gate ticket on screen shows the yard position too |

### 54. Seal mismatch hold (2026-10-17)
| | Item |
|---|---|
| ✅ | Per-terminal setting **Seals at the entry** (`seal_hold`, off by default): a seal read at the entry that isn't the declared one holds the truck on red and puts the case on the trouble desk as **Seal doesn't match** (migration 026); the clerk lets it in with a note, and the barrier opens |
| ✅ | Only a contradiction holds: no seal read, or fewer seals read than declared, never does; without the setting the mismatch is recorded in the inspection as before |
| ✅ | The gate API takes `sealReads` with an arrival, so the check is the gate's own, not the lanes' |

### 55. Edge mode (2026-10-17)
| | Item |
|---|---|
| ✅ | Lane agent (`src/Yardsyncs.Edge`, image `deploy/edge/`): devices talk to it with the same API and keys; it passes everything to the cloud while it answers, and runs the automatic lanes itself from a snapshot when it doesn't |
| ✅ | The same rules offline (`EdgeLaneEngine`, shared with the cloud): reads gathered per truck, matching, gate pass check, arrival window, holds as last seen, blocks, driver identity; same commands; an appointment can't be used twice; exits for trucks inside |
| ✅ | Snapshot (`GET …/gate/edge/snapshot`) and passages (`POST …/gate/edge/passages`) for the agent's gate system key only; passages recorded at the times they happened, once, then the TOS gate transactions and the driver's yard position follow |
| ✅ | Snapshot and unsent passages kept on disk; `GET /edge/status` for monitoring; CI builds and starts the agent image |
| ⬜ | Offline: no TOS re-check, no yard position, no exit check, no trouble desk (a referred truck is handled at the lane). A trouble desk on the agent if a terminal needs it |

### 56. Trucking company API keys (2026-10-17)
| | Item |
|---|---|
| ✅ | A company's own systems (TMS, dispatch) use the API with a key in `X-Api-Key` instead of a person's sign-in; each key acts as its own company user that can't sign in (migration 027) |
| ✅ | Two scopes: **Book and change appointments**, or **Only look** (GET only, `403` otherwise); 300 calls a minute per key (`Api__KeyPerMinute`) |
| ✅ | Made, listed (last used) and revoked by the company admin or a terminal admin in **Fleet & users → API keys**; keys can't manage keys; a suspended company's keys stop |

### 57. Outside review: first fixes (2026-10-17)
| | Item |
|---|---|
| ✅ | Forwarded headers (client address behind the proxy) believed only from the proxy's network (`Proxy__KnownNetworks`, default the private networks), one hop |
| ✅ | Release image scan fails when the scanner can't run (checked in CI); critical problems without a fix are listed for review |
| ✅ | What the lanes do when the TOS, the cloud, the lane agent, a camera or the barrier fails: table in *Connecting lane devices* |
| ✅ | Portal session: short access token in memory and the refresh token in an HttpOnly cookie instead of browser storage (PR after section 57) |
| ✅ | Two-step sign-in (authenticator app), required for admins (`Auth__AdminTwoStep`); reset by another admin |
| ⬜ | Outside penetration test; secret rotation procedure (Phase F) |
| ⬜ | Real N4, hardware and link-failure acceptance tests on the customer's test systems; staging with monitoring and a restore drill |

### 58. Server messages in Arabic (2026-10-17)
| | Item |
|---|---|
| ✅ | The portal sends its language (`Accept-Language`); the server answers check results (TOS and N4 holds, cut-off, VGM), gate decisions and warnings, the ready-to-go reason, trouble-desk reasons, lane messages, container look-ups and errors in Arabic when asked |
| ✅ | Stored texts stay English (the audit trail, trouble cases, the lane devices and the lane agent); they are put into the reader's language on the way out. Container numbers, plates and times are kept as they are; a sentence not in the catalog stays English |
| ✅ | Problems the portal reacts to carry a fixed `code` (`challenge_expired` for a two-step sign-in that took too long) instead of the portal reading the English text |
| ⬜ | Native review of the Arabic wording (with the portal's); the driver app and notifications keep their own texts |

### 59. Reasons in the driver's language (2026-10-17)
| | Item |
|---|---|
| ✅ | The driver app sends its language; the ready-to-go reason, check results (holds, unpaid charges, cut-off, VGM) and the gate's late / early answer come back in Arabic, Urdu or Hindi |
| ✅ | SMS and push messages: the reason from the terminal (e.g. a customs hold) and the slot's day and month in the driver's language, no longer English inside an Arabic message |
| ⬜ | Native review of the Urdu and Hindi wording; portal-only sentences stay English in Urdu and Hindi |

### 60. Messages between the driver and the control room (2026-10-17)
| | Item |
|---|---|
| ✅ | Per visit, both ways: ready-made messages shown in each reader's language (drivers: running late, at the parking, breakdown, container not found, call me; control room: trouble desk, wait in the parking, come to the gate, documents, call us) or a text up to 500 characters |
| ✅ | Portal **Messages** (any staff of the terminal): visits with messages, unread first and counted on the menu; open any visit by its number or from the control room map; the driver's number when they ask to be called |
| ✅ | Driver app: **Messages** on the appointment, unread count on the list; a message from the control room arrives as a push (else SMS / WhatsApp) and opens the conversation |
| ✅ | Open while the visit is live and for a day after its slot; reading marks the other side's messages read; the driver sees "Control room", not a person's name |
| ✅ | The driver shares where the truck is (**Share where I am**); the control room opens it on the map (PR after section 61) |
| ⬜ | Voice notes (`appointment_message.media_url`); ready-made messages in language packs |

### 61. Turn-by-turn inside the terminal (2026-10-17)
| | Item |
|---|---|
| ✅ | Each drive to a stop carries its turns: at junctions only (where roads meet, so there is a choice), from the heading 15 m either side so drawn curves don't count; slight / normal / sharp left and right, U-turn |
| ✅ | A junction is named by the block (or parking, gate, CFS…) within 60 m of it |
| ✅ | App: **Next turn** strip with the arrow (never mirrored in Arabic / Urdu), what to do and how far; spoken once 25–80 m before and once at the turn, in the driver's language (ar, en, ur, hi) |
| ⬜ | Lane-level prompts ("use the left lane") and speed zones (decision 10) |

---

## Remaining

### Phase A — Backend MVP (make it usable by real customers)
| | Item | Notes |
|---|---|---|

### Phase B — Gate
| | Item | Notes |
|---|---|---|
| ✅ | Third-party Autogate / OCR connector | Section 51: the terminal's own gate system uses the gate API with its key. A vendor's own protocol, if it can't call the API, needs a small adapter per customer |

### Phase C — Integrations
| | Item | Notes |
|---|---|---|
| ⬜ | N4 on the customer's system | Install TasLookup, confirm the VERIFY lines, set scope / hold mapping, test against their N4 ❓ test environment access |
| ⬜ | N4 write-back | Pre-advise exports / create truck visits in N4 from bookings ❓ go-ahead needed |
| ⬜ | FASAH customs release | If not already reflected in N4 |
| ⬜ | National appointment platform | Check whether Mawani / FASAH requires integration at the customer's port ❓ |
| ⬜ | SMS / WhatsApp provider account | Choose Unifonic / Twilio / other, register the "Yardsyncs" sender ID ❓ provider |
| ⬜ | Native review of message texts | Arabic, Urdu and Hindi wording; the server texts of sections 58–59 (Arabic for the portal; Arabic, Urdu and Hindi for drivers) |
| ✅ | Trucking company API | Section 56: API keys per company (book, or only look), 300 calls a minute per key |

### Phase D — Operator & company portal (React)
| | Item |
|---|---|
| ✅ | Edit truck plates / driver details and change user roles in the portal (API exists); also company details. The accounts behind API and gate system keys are kept out of user management |
| ✅ | Walk-in booking from the trouble desk; appointment search by container / plate across days (also by number and booking / BL reference) |
| ✅ | Arabic texts for server messages: check results, gate decisions, the ready-to-go reason and errors follow the portal language (section 58) |
| ⬜ | Native review of the portal's Arabic wording |

### Phase E — Driver app (Flutter) — next
| | Item | Notes |
|---|---|---|
| 🔜 | Firebase project and Apple push key per installation (code ready) | Firebase / Apple developer accounts |
| 🔜 | App identity: package name, icon, signing keys, store listings; commit the Android / iOS folders; release builds with HTTPS only | iOS Info.plist: location texts (when in use / always) and background mode "location"; Android 13+: ask for the notification permission so the tracking notice shows; Play Store background-location declaration |
| ⬜ | National e-ID verification as a per-terminal option | Nafath in Saudi Arabia |
| ✅ | Reasons from the TOS in the driver's language: in the app, SMS and push (section 59) | Urdu and Hindi cover what drivers read; native review pending |
| ⬜ | Transfer points (lane / TP) as stops when N4 assigns them instead of a block | `yard_instruction.lane_or_tp` ready |
| ⬜ | Speed zones and no-go lanes on the terminal roads; DXF import (GeoJSON import exists) | ❓ decision 10 |
| ✅ | Turn-by-turn prompts at junctions ("in 50 metres, turn left at block A1"), on screen and spoken (section 61) | |
| ⬜ | Container moved in the yard → push to the driver (today the app re-reads every 30 s and flags the move) | needs push notifications |
| ⬜ | N4 write-back of arrival at block (instead of / besides the webhook) | ❓ customer's N4 |
| ✅ | Messages between the driver and the control room: ready-made ones (in each reader's language) or text, call request, the truck's location; push / SMS to the driver (section 60) | Voice notes still to come |

### Phase F — Production readiness
| | Item |
|---|---|
| ✅ | One Docker image (API + portal) for both hosting modes |
| ✅ | Environments dev / staging / production; image published to a registry with version tags; automatic updates for staging, approved updates for production (`docs/releasing.md`) |
| ⬜ | Staging and cloud servers themselves (machines, domain, secrets in GitHub environments) |
| ⬜ | **Cloud (hosted by us):** one installation serving several terminals, managed PostgreSQL, TLS, automatic updates |
| ✅ | **On-premise (at the terminal):** Docker Compose package with install, upgrade and restore guide (`deploy/`) |
| ⬜ | On-premise: Kubernetes / Helm variant if a terminal asks; rollback of a database upgrade (today: restore the backup taken before) |
| ⬜ | N4 reach from the cloud: secure link to the terminal's N4 (site-to-site VPN, or a small relay agent installed at the terminal that calls N4 and connects out to the cloud) |
| ⬜ | Driver app and trucking companies reach an on-premise installation: public address in the terminal's DMZ, or a cloud relay |
| ⬜ | Licensing for on-premise installations; SQL Server support if a terminal does not allow PostgreSQL ❓ |
| ✅ | Backups, health checks, JSON logging, status for monitoring |
| ⬜ | Alerting wired to the customer's monitoring (rules listed in `deploy/README.md`); off-site copy of backups |
| ✅ | Security review; PDPL privacy: retention, drivers' copy and erasure, location notice (`docs/security.md`) |
| 🟡 | Outside penetration test; two-step sign-in for admins; audit trail screen; secret rotation procedure | Two-step sign-in done (section 57); the rest open |
| ✅ | Load test for peak booking times (CI size every pull request; `peak` profile to run on the customer's staging before go-live) |
| ✅ | User guides (Arabic / English) and onboarding for trucking companies — **Help** in the portal |
| ⬜ | Driver guide in Urdu and Hindi; short videos for drivers |
| ⬜ | **Reminder for Mahmoud when development is finished:** make the GitHub repository private (Settings → Danger Zone → Change visibility) |

### Phase G — Generic product (any terminal operator)
| | Item | Notes |
|---|---|---|
| ✅ | Settings per terminal instead of per installation: TOS adapter, gate identification, driver identity, fees, notification providers and sender names | Sections 34, 41, 42 |
| ⬜ | Online payment of fees (card / SADAD) | Charges are recorded and reported; collecting them needs a payment provider ❓ customer |
| ✅ | Tenants: terminal operator → terminals; operator admins | Section 35. Competitors: separate installations / cloud instances |
| ✅ | Country packs: plate formats, mobile number rules, national holidays, currencies, ID types | Section 36: SA, AE, OM, BH, KW, QA. More countries are new entries in `CountryPacks` with their tests |
| ✅ | Languages as packs: portal and messages in more languages without code changes | Section 37. Help guides stay English / Arabic |
| ✅ | Branding per operator: logo, colours, sender name in messages ("Yardsyncs" by default) | Section 38 |
| ✅ | TOS adapter kit: documented interface + test suite any new TOS adapter must pass | Section 39 |
| ✅ | Terminal onboarding wizard: terminal, move types, weekly plan, lanes, TOS connection, settings above | Section 40; TOS and message settings stay in `.env` |
| 🟡 | Product name in the code and repository: "TAS" → Yardsyncs | Code done (section 43). The GitHub repository name is Mahmoud's to change (Settings → Repository name); GitHub redirects the old address |

### Phase H — Yardsyncs Autogate (next module)
| | Item | Notes |
|---|---|---|
| ✅ | Lane model: lanes, devices per lane (cameras, OCR, RFID reader, kiosk, barrier, traffic light, intercom, weighbridge), lane states | Section 44 |
| ✅ | Device adapters, vendor-neutral: OCR results in (plate, container numbers, ISO code, seal, damage photos), barrier / light / display commands out | Section 45; vendor-specific adapters (a given OCR make's own protocol) per customer |
| ✅ | Lane controller service at the terminal: one transaction per truck — identify, match appointment, re-check, decide, open barrier or send to remote clerk | Section 46; runs inside Yardsyncs, so at the terminal when Yardsyncs is installed there |
| ✅ | Edge mode for cloud-hosted terminals: a small lane agent at the terminal with today's appointments cached, so automatic lanes keep deciding when the link to the cloud drops, and sync back afterwards | Section 55 |
| ✅ | Remote clerk station: live lane view, photos, OCR corrections, intercom, admit / refer / reject — grows out of today's trouble desk | Section 47; the intercom audio itself is the intercom system's |
| ✅ | TOS gate transaction: create the truck visit and gate-in / gate-out in the TOS (N4 gate API), print or send the EIR / gate ticket | Section 48; N4 documents to verify on the customer's N4 test system ❓ test access; sending the ticket to the driver app with push (Phase E) |
| ✅ | Inspection: damage photos and seal check stored with the gate event; disputes with trucking companies | Section 49; holding a truck for a seal mismatch: section 54 |
| ✅ | Exit gate: match outbound container, check delivered against booked, close the visit, turn time | Section 50 |
| ✅ | Lane KPIs: transaction time, OCR read rate, exception rate per lane | Section 52 |
| ✅ | Gate pass from the driver app read at the kiosk; yard position printed or pushed at gate-in | Section 53 |

### Phase I — Next package (agreed with Mahmoud 2026-10-17, in this order)
| | Item | Notes |
|---|---|---|
| ✅ | **Reports 1 — company dashboard**: trucking companies see their own no-shows, on-time rate, time inside per truck and driver, holds and why, charges (**Our performance**); the terminal sees every company side by side under **KPIs → Companies** | Company users only see their company |
| ✅ | **Reports 2 — Excel export**: KPIs (summary, days, hours, companies), a company's report, the appointments of a period with gate times; headings in English or Arabic (right to left) | Charges stay CSV; gate and lane passages are in the appointments file and the lane KPIs |
| ✅ | **Reports 3 — emailed reports**: daily (yesterday + today's bookings, busiest hour) and weekly (the 7 days before, most no-shows) to a list of addresses, English or Arabic, with the KPIs as Excel; sent once at the terminal's hour | Terminal settings `report_hour` (default 07) and `report_weekday` (default Sunday); needs email (SMTP) set up |
| 🔜 | **Driver record**: terminal staff record a penalty or misbehaviour against a driver (speeding, unsafe driving, no PPE, wrong area, damage, abusive conduct, repeated no-show, other), with severity points, date, the visit, a note and a photo | Points expire after N days (per-terminal setting) |
| ⬜ | Driver record warnings: past a threshold of points the control room, the gate and the trouble desk see a warning when the driver's truck comes; optional automatic block past a second threshold | Thresholds and auto-block per terminal; the company sees its own drivers' records (per-terminal setting) |
| ⬜ | **Safety in the app**: safety campaigns (message, picture, period, which drivers / companies, read receipt or "I agree") shown in the driver's language | |
| ⬜ | **Safety induction**: short course with pages and a few questions, required before the first visit and renewed every N months; the gate warns or refers a driver without a valid induction | Per-terminal setting: off / warn / refer; renewal months |
| ⬜ | **AI-optimised slots 1**: suggestions the planner approves — overbooking per hour from real no-shows, trucks per slot from measured gate and yard throughput against a target waiting time, demand forecast against capacity; each with its reason | Needs 4–8 weeks of a terminal's history; per-terminal setting off / suggest / adjust within limits |
| ⬜ | AI-optimised slots 2: slots ranked by expected waiting time when booking ("fast gate" hours) | |
| ⬜ | AI-optimised slots 3: on the day — release no-show capacity to later hours, slow bookings for a congested block | Only with real data; block-level needs N4 equipment data |

---

## Decided
| Date | Decision |
|---|---|
| 2026-10-15 | Hosting: both are supported — cloud service from us and on-premise at the terminal (Phase F) |
| 2026-10-15 | **Yardsyncs is a generic product** sold to many terminal operators. A customer's choice never goes into the code: it is a per-terminal setting (or a pluggable adapter), with a sensible default. The first customer is the first configuration (Phase G) |
| 2026-10-15 | The driver app is **Yardsyncs mobile**, on Android and iPhone, with push notifications |
| 2026-10-15 | The phone is used up to the gate (gate pass, in-gate ticket) and inside the terminal for navigation; when the truck reaches the container's block a message goes to the terminal's systems, for terminals that want it (integration events, per-terminal webhook) |
| 2026-10-15 | **Autogate is the next Yardsyncs module** after the TAS: our own lane application on top of the gate API, vendor-neutral towards cameras, OCR, RFID, kiosks and barriers (Phase H). Everything built for the gate now must stay usable by it |
| 2026-10-17 | **Sold separately or together.** The truck appointment system works alone with the terminal's existing gate system (gate API with a key), or with Yardsyncs Autogate as one ecosystem. Autogate is a separate project the core never references; which terminals have it is a setting (installation and per terminal, switched by system admins) |

## Per-terminal settings (asked when a terminal is set up)
Former open questions, now options each terminal chooses. Default in **bold**.

| Setting | Options | Status |
|---|---|---|
| TOS | **Navis N4 (web services + TasLookup)** · fake TOS for demos · further adapters per customer (other TOS, EDI, database views) | N4 + fake done; adapter chosen per terminal → Phase G |
| National appointment / port community platform | **none** · integration module per country / port when required | Phase C, per customer |
| Driver identity | **mobile one-time code** · national e-ID where available (e.g. Nafath in Saudi Arabia) · ID + selfie check | Phase E |
| Gate identification | **appointment number / QR** · plate OCR · container OCR · RFID tag · geofence check-in — any combination per lane | Gate API accepts all reads; lane devices, manual / automatic per lane and device reads done; matching by RFID tag ⬜ (tags are gathered per truck; the gate doesn't match on them yet) |
| Fees | **free** · booking fee · no-show / late penalty · paid priority slots; payment gateway per country | Phase F (billing) |
| Slot length | **60 min**, 15 / 30 / 120 per terminal; different lengths by time of day as an option | Per terminal done; by time of day ⬜ |
| Yard position source | **TOS position at gate-in** · earlier when the TOS has a pre-advised truck visit (needs TOS write-back) | At gate-in done |
| Terminal map | drawn in the portal's map editor · imported from CAD / GeoJSON | Done |
| Driving with the phone | **voice prompts, screen locked while moving** · view only when stopped | Phase E |
| When the driver sees the yard position | **after gate-in** (in-gate ticket) · when the trip starts | After gate-in done |
| Arrival at the block | **kept as an event** · also sent to the terminal's webhook (chosen event types) · N4 write-back | Webhook done; N4 write-back ⬜ |
| Driver messages | **push to Yardsyncs mobile, SMS fallback** · SMS / WhatsApp only | Done (Firebase per installation) |

## Open decisions
| ❓ | Question | Affects |
|---|---|---|
| 1 | Make the GitHub repository private — decided: do it when development is finished (reminder in Phase F); this workspace cannot change repository settings | Code confidentiality |
