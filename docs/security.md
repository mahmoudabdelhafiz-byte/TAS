# Security and personal data review

First review: 2026-10-16, on the code at the end of Phase F part 1 (production readiness). It covers the server (API,
background jobs, database), the web portal, the driver app's server side and the install kit in `deploy/`. Repeat it
before each major release and after any change to sign-in, permissions or personal data.

**Method.** Every endpoint was read for who may call it and what it returns; sign-in, password links, driver codes,
uploads, logging and the settings a server starts with were read for abuse; the database was read table by table for
personal data and how long it stays. Fixes come with automated tests (`PrivacyApiTests`, `SecurityTests`).

## Findings

| # | Area | Finding | Risk | Status |
|---|---|---|---|---|
| 1 | Password links | Without `PasswordLinks:PortalUrl`, the link in "forgot password" and invitation emails was built from the address the request came to. Anyone could ask for a reset of someone else's account with a forged `Host` header, and the victim's email would carry a link to the attacker's site; clicking it hands over the reset token. | High | **Fixed.** On a real server links are built only from `PasswordLinks:PortalUrl` (set by `deploy/`); without it no link is sent and `/api/ops/status` warns. The request's address is used only on a developer's machine and in tests. |
| 2 | Logs | With no SMS or email provider configured (the `Log` default), the full text of every message went to the log, including driver sign-in codes and password links. Anyone who can read the logs could sign in as a driver or reset a password. | High | **Fixed.** On a real server the log shows the channel, a masked recipient (`+9••••••••001`) and the subject only. `Notifications:LogMessageText=true` brings the text back for a demo server, with a warning in the status. |
| 3 | Driver sign-in | Wrong code tries were counted in memory and saved afterwards: tries sent at the same moment could exceed the 5-try limit; the right code sent twice at once could sign in twice. | Medium | **Fixed.** Tries are counted and the code consumed with single atomic updates; at most 10 codes per number per day on top of 3 per 15 minutes. |
| 4 | Portal sign-in | Same pattern for failed password logins: parallel guesses could exceed the 5 failures before lock-out. | Low (also limited per client address) | **Fixed.** Failures counted with an atomic update. |
| 5 | Test switches | `DriverLogin:ExposeCodeForTesting` and `PasswordLinks:ExposeTokenForTesting` return codes and links in API answers for the automated tests. Turned on by mistake on a real server they would give away every account. | Medium | **Fixed.** The app refuses to start with either on outside Development and Testing. |
| 6 | Bulk upload | An `.xlsx` is a zip: a file under the 2 MB limit can unpack to gigabytes and take the server down; a sheet with a million rows or thousands of columns would also be read in full. | Medium | **Fixed.** Workbooks that unpack to more than 40 MB are refused (413); at most 40 columns and one row past the 500-row limit are read; the request body is capped at the upload limit. |
| 7 | Personal data kept forever | Phone positions, message recipients, events sent to the terminal's systems, sign-in codes, old sessions (with IP addresses) and push addresses of old phones were never removed. | PDPL | **Fixed.** Daily clean-up, see [Retention](#retention). |
| 8 | Data subject rights | No way to give a driver a copy of their data or to erase it. | PDPL | **Fixed.** Copy and erasure for drivers, see [Rights](#rights-of-drivers). |
| 9 | Notice | The driver app shared positions without telling the driver what for and for how long. | PDPL | **Fixed.** Before the first trip the app explains what is shared, why, until when, and that data can be copied or erased; the trip starts only after the driver agrees. |
| 10 | Client address | Behind the reverse proxy the app trusts `X-Forwarded-For` for rate limits. If the app's port 8080 were reachable directly, a client could set any address and escape the per-client limits. | Low (setup) | **By design**: `deploy/` publishes only the proxy's ports. Kept in the install guide: never publish 8080. |

## Checked and in order

- **Who may do what.** Every endpoint requires sign-in except login, refresh, forgot password, password link, driver
  sign-in and health. Staff and driver tokens have separate audiences: a driver token opens no portal endpoint and the
  reverse (tested). Each request re-checks the account in the database, so deactivating a user or changing their role
  takes effect at once. Company users see only their own company's appointments, drivers and trucks (others answer
  404 or 403); terminal staff only their terminal. Cancel, move and assign go through the same company check.
- **Passwords and tokens.** Passwords are hashed with ASP.NET Core Identity (PBKDF2); unknown emails take the same time
  as wrong passwords. Refresh tokens rotate; re-using a revoked one ends every session of the account. Password links,
  refresh tokens and driver codes are stored only as SHA-256 hashes; links work once and expire (60 minutes / 7 days).
  Setting a password ends every session.
- **Gate pass.** The rotating QR secret is encrypted at rest (AES-GCM, `GatePass:Key`); a new sign-in revokes the old
  phone's pass.
- **Database access.** All SQL is parameterised (EF Core or interpolated `SqlQuery`/`ExecuteSql`, which become
  parameters); no SQL is built by string concatenation.
- **Browser.** Content-Security-Policy (scripts only from the app), no framing, `nosniff`, referrer policy, HSTS
  outside development. The portal sends its token in the `Authorization` header, not in a cookie, so cross-site
  request forgery does not apply. The session is kept in the browser's local storage so dispatchers stay signed in;
  the strict script policy is what keeps other scripts from reading it, and shared office PCs should sign out.
- **Webhooks to the terminal's systems** are signed (HMAC-SHA256 with timestamp) and their addresses come only from the
  server's settings, never from users.
- **Secrets** live in environment variables (`deploy/.env`, not in the repository); the app refuses to start without
  a sign-in key of at least 32 bytes.

## Personal data (Saudi PDPL)

What Yardsyncs holds about people, and why:

| Who | Data | Purpose | Kept |
|---|---|---|---|
| Portal users | Name, email, mobile, role, last sign-in, sign-in IP (sessions) | Accounts and the audit trail | While the account exists; sessions 30 days after they end |
| Drivers | Name, ID / iqama number, mobile, language | Identify the driver at booking and at the gate; send them their appointment | While the company keeps the driver; erasure on request |
| Drivers | Phone: platform, app version, push address | Sign-in on one phone; push messages | Push address removed once the phone is no longer the driver's active one |
| Drivers | Positions — only from "I am leaving now" until leaving the terminal | Arrival time, automatic check-in at the pre-gate, directions in the yard, control room | Coordinates 30 days (`Privacy:LocationDays`); the times stay for the statistics |
| Drivers and users | Where messages went (number / email) and their values | Delivering reminders, alerts, sign-in codes | 180 days (`Privacy:MessageDays`); the record stays for the counts |
| Drivers | Events sent to the terminal's systems (truck at block, with position and plate) | The terminal's own operations, when it asks for them | 90 days (`Privacy:EventDays`) |

Positions are taken only while a visit is under way: the server refuses them before the trip starts and after the
truck has arrived (on the way) or left the terminal.

### Retention

A background job (`RetentionService`) runs a minute after start and then daily. It clears coordinates older than
`Privacy:LocationDays`, message recipients and values older than `Privacy:MessageDays`, delivered or failed events
older than `Privacy:EventDays`, sign-in codes older than a day, ended sessions and used or expired password links older
than `Privacy:TokenDays`, and push addresses of phones that are no longer in use. Visits, appointments and the audit
trail stay. The last run and what it removed are shown in `/api/ops/status`.

### Rights of drivers

In the portal, **Fleet & users → Drivers**, the company's admin or a terminal admin can:

- **Copy of data** — a file with everything held about the driver: record, phones, verifications, gate passes, visits,
  positions still held, messages, the number of events sent to the terminal's systems, and the retention periods.
  API: `GET /api/companies/{companyId}/drivers/{driverId}/personal-data`.
- **Erase personal data** — with a reason, after a warning. Removes name, ID number and mobile (the driver becomes
  "Erased driver" and is blocked), the phones and their push addresses, gate passes, positions, message addresses,
  events sent to the terminal's systems, and the values in the audit trail (which keeps that the erasure happened, by
  whom and why). Visits stay as the terminal's and the company's records, without the person. Refused while the driver
  has a visit that isn't over. API: `POST /api/companies/{companyId}/drivers/{driverId}/erase` with `{ "reason": "…" }`.

Portal users are corrected or removed by their administrators (deactivation); a copy of a user's data is a short
database query today and gets a screen when a customer needs it.

### What each customer decides and does

- **Where the data lives.** On-premise keeps it at the terminal. For the cloud service, host in a Saudi region; the
  PDPL limits transfers abroad.
- **Transfers abroad by providers.** Push messages go through Google Firebase and SMS / email through the chosen
  provider; they carry the appointment number, times and block, not ID numbers. Name these processors in the
  customer's privacy notice, or choose providers hosted in the Kingdom.
- **Privacy notice and record of processing** for drivers and company users, using the table above.
- **Breaches**: report to SDAIA within 72 hours of becoming aware, and tell the people affected when there is harm.
  The audit trail, the JSON logs and the sign-in warnings in them are the starting points.
- **Disk encryption and off-site backups** on the server (the database and `deploy/backups/`).

## Not done yet

| Item | Why it matters |
|---|---|
| Penetration test by an outside firm before the first go-live | A second pair of eyes; often a customer requirement |
| Dependency scanning in CI (Dependabot / `dotnet list package --vulnerable`, `npm audit`) | Known holes in libraries |
| Two-step sign-in for terminal admins and system admins | Admin accounts can change everything |
| Audit trail screen and export | Investigations without database access |
| Secret rotation procedure (sign-in key, gate pass key, provider keys) | After staff changes or a leak |
| Copy of data for portal users as a screen | Same right as drivers; today a query |
