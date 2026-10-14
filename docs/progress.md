# Yardsyncs — Progress Log

Last updated: 2026-10-15 (after the booking screen)

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

### 12. Notification sender (2026-10-14)
| | Item |
|---|---|
| ✅ | Outbox delivery every 30 s; messages queued in the same transaction as the change that causes them |
| ✅ | Templates in Arabic, English, Urdu, Hindi (SMS short, email with details); failing containers and problems named in the recipient's language |
| ✅ | Templates: STATUS_RED, STATUS_CLEARED, SLOT_REMINDER (2 h before; dispatcher if no driver yet), SLOT_CLOSED (terminal closed a booked slot) |
| ✅ | Re-checked before sending: skipped if cancelled / done, slot passed, superseded by a newer status, or older than 12 h |
| ✅ | Retries with back-off for transient errors (max 5 attempts); permanent errors fail at once |
| ✅ | Safe with several API instances: rows claimed with FOR UPDATE SKIP LOCKED and a lease |
| ✅ | Providers: generic HTTP SMS gateway (any Saudi provider by configuration), SMTP email (RTL HTML for Arabic / Urdu), Log for development; None by default so missing setup is visible |
| ✅ | API: notification log for terminal staff with filters; re-send failed messages (planners / admins) |
| ✅ | Database: recipient, attempts, next attempt, last error, SKIPPED status; upgrade script `db/migrations/005_notifications.sql` |
| ✅ | Tests: renderer and rules unit tests, SMS gateway / email against a fake server, end-to-end outbox flows incl. concurrent senders, API |

### 13. Password reset & invitations (2026-10-14)
| | Item |
|---|---|
| ✅ | Forgot password: email with a one-time link (60 min); same answer for unknown emails; max 3 requests per user per hour |
| ✅ | Invitations: admins create users without a password; the user sets it from an emailed link (7 days); resend cancels the old link |
| ✅ | Set password with a link: policy checked first (a typo doesn't burn the link); unlocks the account; signs out everywhere; audited |
| ✅ | Links stored only as SHA-256 hashes; waiting in the outbox encrypted (AES-GCM) and wiped once sent / failed / skipped |
| ✅ | Newer link cancels older ones; change password, admin reset and deactivation cancel open links; parallel use of one link succeeds once |
| ✅ | Emails in Arabic, English, Urdu, Hindi with the company / terminal name; links to the portal (`Notifications:PortalUrl`) |
| ✅ | Database: `password_token`, `notification_log.payload`; upgrade script `db/migrations/006_password_tokens.sql` |
| ✅ | Tests: email texts, encryption, full flows incl. lockout, limits, expiry, invitations, concurrency, API |

### 14. Database migrations (2026-10-14)
| | Item |
|---|---|
| ✅ | Decision: versioned SQL scripts with a built-in runner instead of EF-generated migrations — EF maps 27 of 36 tables and none of the PostGIS columns, so generated migrations would drop tables and most CHECK constraints / partial indexes |
| ✅ | `db/migrations/001_initial.sql` (v0.2 from history) + 002–006 rebuild today's schema exactly (verified table by table) |
| ✅ | Runner compiled into the API: `schema_migration` history with checksums, one transaction per script, advisory lock for concurrent instances |
| ✅ | Safety: edited applied scripts, out-of-order scripts and databases newer than the build are refused with a clear message |
| ✅ | Existing databases (from `schema.sql` or the manual scripts) recognised and baselined automatically |
| ✅ | `Tas.Api migrate` / `migrate status` for deployment pipelines; `Database:Migrations` = Apply / Verify / Off on start |
| ✅ | `db/schema.sql` kept as a readable snapshot; CI fails if it drifts from the migrations |
| ✅ | CI creates its test databases with the same `migrate` command used in deployments |
| ✅ | Tests: script naming / ordering / checksums; fresh, baseline, partial upgrade, edited script, older build, failing script rollback, 4 parallel instances |

### 15. Web portal foundation (2026-10-14)
| | Item |
|---|---|
| ✅ | `portal/`: React + TypeScript (Vite), React Router, TanStack Query; served by the API from `Portal:Path` (one address, strict CSP) |
| ✅ | Arabic first (RTL) with English; typed texts (a missing Arabic text fails the build); Gregorian dates, Latin digits, terminal time zone |
| ✅ | Sign in, forgot password, set password / accept invitation (token from the URL fragment, removed after reading) |
| ✅ | Appointments of a day: traffic light as a coloured row edge, counts per light, refresh every minute; stacked blocks on phones |
| ✅ | Users (admins): invite, resend invitation, deactivate / reactivate; pending invitations shown |
| ✅ | My account: language of emails and SMS, change password (stays signed in on this device) |
| ✅ | Sessions: access token in memory, rotating refresh token, refreshes serialised across browser tabs |
| ✅ | API: `GET /api/terminals`, `PUT /api/auth/me/language`, `passwordSet` on users |
| ✅ | CI: type check, 16 unit tests, build; Playwright end-to-end tests against the real API on desktop and phone; screenshots pushed to the `ci-screenshots/<branch>` branch |

### 16. Booking screen and appointment page (2026-10-15)
| | Item |
|---|---|
| ✅ | Book a visit: company (terminal staff), 1–4 moves by container or cargo release, booking reference, truck and driver now or later |
| ✅ | Container numbers checked (ISO 6346, same rules as the API) while typing — a typo is caught before anything is sent |
| ✅ | Slot board: each hour with places left for the chosen type; full / closed shown; keyboard arrows; booking horizon respected |
| ✅ | A refused booking lists the failed checks by name in Arabic / English, with the terminal system's own message |
| ✅ | Appointment page: light and reason, checks per move, check again, assign / change truck and driver, move to another time, cancel (cut-off enforced by the API, explained in the page) |
| ✅ | Phones: no page wider than the screen (enforced by the e2e tests, which name the offending element); appointments and users stack into blocks |
| ✅ | Tests: 25 portal unit tests; e2e books, moves and cancels through the UI on desktop and phone |

---

## Remaining

### Phase A — Backend MVP (make it usable by real customers)
✅ Complete (sections 5–14). Fees / billing wait for open decision 7.

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
| ⬜ | SMS provider account | Pick the customer's SMS gateway, register the sender name with the CST, fill `Notifications:Sms:Http` ❓ which provider |
| ⬜ | WhatsApp / push channels | WhatsApp Business templates (pre-approved per language); push comes with the driver app (Phase E) |
| ⬜ | Trucking company API | Client credentials, scopes, rate limits for bookings from their own TMS |

### Phase D — Operator & company portal (React)
| | Item |
|---|---|
| 🔜 | **Bulk upload and fleet**: upload the Excel / CSV booking sheet with the dry-run preview; trucks and drivers for company admins |
| ⬜ | Terminal screens: capacity setup (weekly grid, seasonal templates, overrides calendar), slot board for planners, gate board |
| ⬜ | Reasons in the user's language: API returns reason codes + values (today the traffic-light reasons and server errors are English only) |
| ⬜ | Capacity setup: weekly template grid, seasonal templates, overrides calendar |
| ⬜ | Slot board: capacity vs booked per hour/category, over-capacity alerts |
| ⬜ | Appointments: search, details, cancel / move on behalf of companies |
| ⬜ | Company portal: availability, booking (single / dual / cargo), bulk upload, fleet (trucks, drivers) |
| ⬜ | KPI dashboard: slot utilization, no-show rate, turn time, appointment vs walk-in |
| ⬜ | Arabic / English UI (RTL) |

### Phase E — Driver app (Flutter)
| | Item |
|---|---|
| ⬜ | Login with mobile OTP; driver verification (OTP / Nafath ❓) |
| ⬜ | My appointments with traffic light and reason; Arabic, English, Urdu, Hindi |
| ⬜ | Digital gate pass (rotating QR) |
| ⬜ | Start trip → geofence ETA and auto check-in (reuse truck tracking work) |
| ⬜ | Yard guidance after gate-in (block / lane / transfer point from N4) |
| ⬜ | Chat / call request to control room, templated messages |
| ⬜ | Backend for the above: devices, verification, gate pass, geofences, ETA, yard instructions, messages (tables already in the schema) |

### Phase F — Production readiness
| | Item |
|---|---|
| ⬜ | Hosting & deployment (Docker, environments: dev / staging / production) |
| ⬜ | Backups, monitoring, logging, alerting |
| ⬜ | Security review; PDPL privacy (location only during active visits, consent) |
| ⬜ | Load test for peak booking times |
| ⬜ | User guides (Arabic / English) and onboarding for trucking companies |

---

## Open decisions
| ❓ | Question | Affects |
|---|---|---|
| 1 | Make the GitHub repository private? (currently public) | Code confidentiality |
| 2 | Customer hosting: cloud service from us, or on-premise at the terminal? | Database, deployment, pricing |
| 3 | N4 integration path: built on web services (genericInvoke + Groovy) — confirm this suits the customer, or EDI / DB views instead? | Phase C |
| 4 | Is a national appointment platform mandatory at the customer's port? | Phase C |
| 5 | Driver identity: mobile OTP enough, or Nafath / liveness required for ISPS? | Phase E |
| 6 | Gate check-in: QR scan at lane, or geofence + OCR plate match only? | Phases B, E |
| 7 | Fees: free booking, paid booking, or penalties for no-show / late only? | Phase A, billing |
| 8 | Slot length: fixed per terminal, or different lengths by time of day? | Capacity engine |
| 9 | Can N4 provide yard positions per truck visit in real time? | Yard guidance |
| 10 | Which SMS provider does the customer use (or should we contract one)? Urdu / Hindi texts reviewed by a native speaker? | Notifications |
