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
| ⬜ | Autogate / OCR adapter | Connect the customer's specific lane system to the gate API (push from lanes or polling) ❓ which system |
| ⬜ | Gate pass for drivers | Rotating QR in the driver app instead of the static appointment QR (Phase E) |

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
| 🔜 | Capacity setup: weekly template grid, seasonal templates, reservations, release rules |
| 🔜 | Bulk upload screen (dry run, results per row, template download) |
| 🔜 | Fleet (trucks, drivers) and user management screens; companies for terminal admins |
| 🔜 | KPI dashboard: slot utilization, no-show rate, turn time, appointment vs walk-in |
| ⬜ | Walk-in booking from the trouble desk; appointment search by container / plate across days |
| ⬜ | Arabic texts for server messages (check results and errors are English today) |
| ⬜ | Native review of the portal's Arabic wording |

### Phase E — Driver app (Flutter) — next after portal round 2
| | Item | Notes |
|---|---|---|
| 🔜 | App skeleton in `mobile/`: Flutter (Android first, iOS after), Arabic / English / Urdu / Hindi, right-to-left; CI job (analyze, tests, build APK) | Same repository and CI as the API and portal |
| ⬜ | Login with mobile OTP (SMS through the notification providers); driver verification | ❓ Nafath (decision 5) |
| ⬜ | My appointments: hour, moves, traffic light and reason in the driver's language; push when it changes | Replaces SMS for drivers who have the app |
| ⬜ | Gate pass: rotating QR for the lane reader; after gate-in it shows where to go (block, row, bay or transfer point) | `gate_pass` table |
| ⬜ | Start trip → ETA to the terminal, auto check-in at the pre-gate geofence | Reuse the truck tracking work; location only while EN_ROUTE / inside |
| ⬜ | **Yard navigation:** at gate-in TAS reads the container's yard position from N4 and turns it into stops (`yard_instruction`): import pickup → block / row / bay; export drop / empty return → block or transfer point N4 assigns; dual moves → stops in order, then exit | N4 TasLookup adds the position; ❓ decision 9 |
| ⬜ | **Terminal map:** blocks, roads (one-way, speed zones), transfer points, gates and exits as a map layer; routes computed on the terminal's own road network (public maps don't know internal roads); works without mobile data inside the yard | Built from the terminal's CAD drawing / survey; editor in the portal ❓ decision 10 |
| ⬜ | Route on the map from the gate to the block, next turn in large text, voice prompts in the driver's language; the screen does not take input while the truck moves | ❓ decision 11 (phone use inside the terminal) |
| ⬜ | Container moved in the yard (rehandle / housekeeping) → new position pushed to the driver, route redrawn | N4 change detected by re-reading the position on a short interval while the truck is inside |
| ⬜ | Arrived at the block (geofence) → shown to the control room / gate board; time per stop for turn-time analysis | `geofence`, `geofence_event` tables |
| ⬜ | Chat / call request to the control room, templated messages | `appointment_message` table |
| ⬜ | Backend for the above: devices, verification, gate pass, ETA, yard instructions, map data, messages | Tables already in the schema |

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

---

## Decided
| Date | Decision |
|---|---|
| 2026-10-15 | Hosting: both are supported — cloud service from us and on-premise at the terminal (Phase F) |

## Open decisions
| ❓ | Question | Affects |
|---|---|---|
| 1 | Make the GitHub repository private? (currently public) | Code confidentiality |
| 3 | N4 integration path: built on web services (genericInvoke + Groovy) — confirm this suits the customer, or EDI / DB views instead? | Phase C |
| 4 | Is a national appointment platform mandatory at the customer's port? | Phase C |
| 5 | Driver identity: mobile OTP enough, or Nafath / liveness required for ISPS? | Phase E |
| 6 | Gate check-in: QR scan at lane, or geofence + OCR plate match only? | Phases B, E |
| 7 | Fees: free booking, paid booking, or penalties for no-show / late only? | Phase A, billing |
| 8 | Slot length: fixed per terminal, or different lengths by time of day? | Capacity engine |
| 9 | Can N4 provide yard positions per truck visit in real time? For exports and empty returns the block is only known once N4 plans the receipt — at gate-in, or earlier if TAS pre-advises the truck visit in N4 (needs the N4 write-back go-ahead) | Yard navigation |
| 10 | Terminal map source: CAD drawing / survey of blocks, roads and transfer points — who provides it, and who keeps it up to date when the layout changes? | Yard navigation |
| 11 | Phone use while driving inside the terminal: does the terminal's safety policy allow a phone in a holder with voice prompts, or only a view when the truck is stopped? | Yard navigation |
| 12 | Show the yard position only after gate-in (recommended: the container's location is not given to trucks outside the terminal)? | Yard navigation, security |
