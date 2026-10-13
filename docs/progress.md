# TAS — Progress Log

Last updated: 2026-10-14

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

---

## Remaining

### Phase A — Backend MVP (make it usable by real customers)
| | Item | Notes |
|---|---|---|
| 🔜 | **Authentication & roles** | Company users see/book only their own appointments; terminal staff roles (planner, gate clerk, admin). Replaces `userId` in request bodies |
| ⬜ | Capacity admin API | Templates, seasonal templates, overrides (with reason + audit log), reservations, quota release rules, terminal settings |
| ⬜ | Master data API | Companies, users, trucks, drivers, categories, cargo allocations |
| ⬜ | Audit log | Every capacity/settings change: who, when, old/new values, reason |
| ⬜ | Bulk booking | CSV/Excel upload for dispatchers |
| ⬜ | Re-validation before arrival | Re-check TOS status of booked items periodically and when the driver starts the trip; update traffic light |
| ⬜ | EF Core migrations | Replace `db/schema.sql` as the source of truth |

### Phase B — Gate
| | Item | Notes |
|---|---|---|
| ⬜ | Gate-in matching | OCR plate / container read → appointment; early / late / no appointment handling |
| ⬜ | Gate events & statuses | ARRIVED → GATE_IN → COMPLETED, REJECTED_AT_GATE; turn time |
| ⬜ | Trouble desk | Exceptions queue with the reason already shown |
| ⬜ | Walk-in handling | Book on the spot into reserved capacity |

### Phase C — Integrations
| | Item | Notes |
|---|---|---|
| ⬜ | **Navis N4 adapter** | Validation (holds, availability, releases), pre-advice / truck visit creation, gate events. Web services or EDI ❓ |
| ⬜ | FASAH customs release | If not already reflected in N4 |
| ⬜ | National appointment platform | Check whether Mawani / FASAH requires integration at the customer's port ❓ |
| ⬜ | Notifications | SMS / WhatsApp / push: reminders, container ready, status turned red, slot closed |
| ⬜ | Trucking company API | Client credentials, scopes, rate limits for bookings from their own TMS |

### Phase D — Operator & company portal (React)
| | Item |
|---|---|
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
| 3 | N4 integration path: web services, EDI, or database views? | Phase C |
| 4 | Is a national appointment platform mandatory at the customer's port? | Phase C |
| 5 | Driver identity: mobile OTP enough, or Nafath / liveness required for ISPS? | Phase E |
| 6 | Gate check-in: QR scan at lane, or geofence + OCR plate match only? | Phases B, E |
| 7 | Fees: free booking, paid booking, or penalties for no-show / late only? | Phase A, billing |
| 8 | Slot length: fixed per terminal, or different lengths by time of day? | Capacity engine |
| 9 | Can N4 provide yard positions per truck visit in real time? | Yard guidance |
