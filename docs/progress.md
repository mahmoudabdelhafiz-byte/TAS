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

---

## Remaining

### Phase A — Backend MVP (make it usable by real customers)
| | Item | Notes |
|---|---|---|
| ⬜ | Password reset | Forgotten-password flow (email or SMS), invited users set their own password |
| ⬜ | EF Core migrations | Replace `db/schema.sql` as the source of truth |

### Phase B — Gate
| | Item | Notes |
|---|---|---|
| ⬜ | Third-party Autogate / OCR connector | For terminals that keep their existing lane system: push its reads into the gate API. Our own Autogate is Phase H |

### Phase C — Integrations
| | Item | Notes |
|---|---|---|
| ⬜ | N4 on the customer's system | Install TasLookup, confirm the VERIFY lines, set scope / hold mapping, test against their N4 ❓ test environment access |
| ⬜ | N4 write-back | Pre-advise exports / create truck visits in N4 from bookings ❓ go-ahead needed |
| ⬜ | FASAH customs release | If not already reflected in N4 |
| ⬜ | National appointment platform | Check whether Mawani / FASAH requires integration at the customer's port ❓ |
| ⬜ | SMS / WhatsApp provider account | Choose Unifonic / Twilio / other, register the "Yardsyncs" sender ID ❓ provider |
| ⬜ | Native review of message texts | Arabic, Urdu and Hindi wording; translated reasons (reasons from the TOS are in English today) |
| ⬜ | Trucking company API | Client credentials, scopes, rate limits for bookings from their own TMS |

### Phase D — Operator & company portal (React)
| | Item |
|---|---|
| ⬜ | Edit truck plates / driver details and change user roles in the portal (API exists); password reset by the user (Phase A) |
| ⬜ | Walk-in booking from the trouble desk; appointment search by container / plate across days |
| ⬜ | Arabic texts for server messages (check results and errors are English today) |
| ⬜ | Native review of the portal's Arabic wording |

### Phase E — Driver app (Flutter) — next
| | Item | Notes |
|---|---|---|
| 🔜 | Firebase project and Apple push key per installation (code ready) | Firebase / Apple developer accounts |
| 🔜 | App identity: package name, icon, signing keys, store listings; commit the Android / iOS folders; release builds with HTTPS only | iOS Info.plist: location texts (when in use / always) and background mode "location"; Android 13+: ask for the notification permission so the tracking notice shows; Play Store background-location declaration |
| ⬜ | National e-ID verification as a per-terminal option | Nafath in Saudi Arabia |
| ⬜ | Reasons from the TOS in the driver's language (English today) | |
| ⬜ | Transfer points (lane / TP) as stops when N4 assigns them instead of a block | `yard_instruction.lane_or_tp` ready |
| ⬜ | Speed zones and no-go lanes on the terminal roads; DXF import (GeoJSON import exists) | ❓ decision 10 |
| ⬜ | Turn-by-turn prompts at junctions ("turn left at block A1"); spoken stops exist | |
| ⬜ | Container moved in the yard → push to the driver (today the app re-reads every 30 s and flags the move) | needs push notifications |
| ⬜ | Control-room view in the portal: trucks inside, stop reached / done, time per stop for turn-time analysis | events and stop times are recorded |
| ⬜ | N4 write-back of arrival at block (instead of / besides the webhook) | ❓ customer's N4 |
| ⬜ | Chat / call request to the control room, templated messages | `appointment_message` table |
| ⬜ | Backend for messages | `appointment_message` table |

### Phase F — Production readiness
| | Item |
|---|---|
| ⬜ | One set of Docker images (API + portal) for both hosting modes; environments dev / staging / production |
| ⬜ | **Cloud (hosted by us):** one installation serving several terminals, managed PostgreSQL, TLS, automatic updates |
| ⬜ | **On-premise (at the terminal):** install package for the terminal's servers (Docker Compose or Kubernetes), runs without internet except for SMS / WhatsApp / push and updates; upgrade and rollback guide |
| ⬜ | N4 reach from the cloud: secure link to the terminal's N4 (site-to-site VPN, or a small relay agent installed at the terminal that calls N4 and connects out to the cloud) |
| ⬜ | Driver app and trucking companies reach an on-premise installation: public address in the terminal's DMZ, or a cloud relay |
| ⬜ | Licensing for on-premise installations; SQL Server support if a terminal does not allow PostgreSQL ❓ |
| ⬜ | Backups, monitoring, logging, alerting |
| ⬜ | Security review; PDPL privacy (location only during active visits, consent) |
| ⬜ | Load test for peak booking times |
| ⬜ | User guides (Arabic / English) and onboarding for trucking companies |
| ⬜ | **Reminder for Mahmoud when development is finished:** make the GitHub repository private (Settings → Danger Zone → Change visibility) |

### Phase G — Generic product (any terminal operator)
| | Item | Notes |
|---|---|---|
| ⬜ | Settings per terminal instead of per installation: TOS adapter, gate identification, driver identity, fees, notification providers and sender names | Today `Tos:Adapter` and the providers are set once per installation |
| ⬜ | Tenants: terminal operator → terminals; operator admins; data separated per operator in the cloud | Terminal scope already exists |
| ⬜ | Country packs: plate formats (Saudi Arabic plates today), mobile number rules (+966 today), national holidays, currencies, ID types | Plate and mobile rules become per-country |
| ⬜ | Languages as packs: portal and messages in more languages without code changes | Arabic, English, Urdu, Hindi today |
| ⬜ | Branding per operator: logo, colours, sender name in messages ("Yardsyncs" by default) | |
| ⬜ | TOS adapter kit: documented interface + test suite any new TOS adapter must pass | `ITosAdapter` today |
| ⬜ | Terminal onboarding wizard: terminal, move types, weekly plan, lanes, TOS connection, settings above | |
| ⬜ | Product name in the code and repository: "TAS" → Yardsyncs | |

### Phase H — Yardsyncs Autogate (next module)
| | Item | Notes |
|---|---|---|
| ⬜ | Lane model: lanes, devices per lane (cameras, OCR, RFID reader, kiosk, barrier, traffic light, intercom, weighbridge), lane states | `gate_lane` today |
| ⬜ | Device adapters, vendor-neutral: OCR results in (plate, container numbers, ISO code, seal, damage photos), barrier / light / display commands out | The gate API already takes plate and container reads |
| ⬜ | Lane controller service at the terminal: one transaction per truck — identify, match appointment, re-check, decide, open barrier or send to remote clerk | On-premise next to the lanes, keeps working if the cloud link drops |
| ⬜ | Remote clerk station: live lane view, photos, OCR corrections, intercom, admit / refer / reject — grows out of today's trouble desk | |
| ⬜ | TOS gate transaction: create the truck visit and gate-in / gate-out in the TOS (N4 gate API), print or send the EIR / gate ticket | Needs TOS write access |
| ⬜ | Inspection: damage photos and seal check stored with the gate event; disputes with trucking companies | |
| ⬜ | Exit gate: match outbound container, check delivered against booked, close the visit, turn time | Gate-out exists in the API |
| ⬜ | Lane KPIs: transaction time, OCR read rate, exception rate per lane | |
| ⬜ | Gate pass from the driver app read at the kiosk; yard position printed or pushed at gate-in | Phase E |

---

## Decided
| Date | Decision |
|---|---|
| 2026-10-15 | Hosting: both are supported — cloud service from us and on-premise at the terminal (Phase F) |
| 2026-10-15 | **Yardsyncs is a generic product** sold to many terminal operators. A customer's choice never goes into the code: it is a per-terminal setting (or a pluggable adapter), with a sensible default. The first customer is the first configuration (Phase G) |
| 2026-10-15 | The driver app is **Yardsyncs mobile**, on Android and iPhone, with push notifications |
| 2026-10-15 | The phone is used up to the gate (gate pass, in-gate ticket) and inside the terminal for navigation; when the truck reaches the container's block a message goes to the terminal's systems, for terminals that want it (integration events, per-terminal webhook) |
| 2026-10-15 | **Autogate is the next Yardsyncs module** after the TAS: our own lane application on top of the gate API, vendor-neutral towards cameras, OCR, RFID, kiosks and barriers (Phase H). Everything built for the gate now must stay usable by it |

## Per-terminal settings (asked when a terminal is set up)
Former open questions, now options each terminal chooses. Default in **bold**.

| Setting | Options | Status |
|---|---|---|
| TOS | **Navis N4 (web services + TasLookup)** · fake TOS for demos · further adapters per customer (other TOS, EDI, database views) | N4 + fake done; adapter chosen per terminal → Phase G |
| National appointment / port community platform | **none** · integration module per country / port when required | Phase C, per customer |
| Driver identity | **mobile one-time code** · national e-ID where available (e.g. Nafath in Saudi Arabia) · ID + selfie check | Phase E |
| Gate identification | **appointment number / QR** · plate OCR · container OCR · RFID tag · geofence check-in — any combination per lane | Gate API accepts all reads today; lane setup → Phase H |
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
