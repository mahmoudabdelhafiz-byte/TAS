# Installing and running Yardsyncs

For the terminal's IT team. The full step-by-step guide comes with the install kit (`deploy/README.md`).

## What it needs

- A Linux server with Docker and Docker Compose: 4 CPU, 8 GB memory and 100 GB disk for a mid-size terminal.
- A name such as `tas.terminal.com` and ports 80 and 443. Drivers' phones reach the server over mobile data, so the
  address must be reachable from the internet (only the HTTPS proxy's ports).
- An SMTP account for email and an SMS provider (Unifonic or Twilio); for push messages to the app, a Firebase project.
- For the TOS link: the N4 web services address and an account (read-only lookups).

## Install

1. Unpack the install kit of the version (from the release page) on the server.
2. Copy `.env.example` to `.env` and fill it in: the address, the database password, the sign-in key and the gate
   pass key (random values), the first administrator, the TOS and the message providers.
3. `docker compose up -d`. The database is created on the first start; `https://<address>/health/ready` answers
   when it is ready.
4. Sign in as the first administrator, change the password, then set up the terminal, its users and capacity.

HTTPS: a free certificate from Let's Encrypt, the terminal's own certificate, or, on a network without internet, a
certificate from the proxy's own authority (installed on the terminal's PCs).

## Several terminals on one installation

One installation (our cloud service, or an operator's own server) can serve several terminals, each with settings of
its own, by terminal code. Whatever a terminal doesn't set comes from the installation's settings:

```
Terminals__JED1__Tos=N4                                     the TOS: N4 or Fake (demo data); default Tos__Adapter
N4__Terminals__JED1__Url=...                                 that terminal's N4 connection
Terminals__JED1__Brand=Harbor One Terminal                       the name messages are signed with; default Brand, then Yardsyncs
Terminals__JED1__Country=AE                                  public holidays and currency; default Country
Terminals__JED1__Notifications__DriverChannel=Whatsapp       drivers without the app: Sms or Whatsapp
Terminals__JED1__Notifications__SmsProvider=Unifonic         any message setting: provider, account,
Terminals__JED1__Notifications__Unifonic__SenderId=HARBOR1      SMS sender name,
Terminals__JED1__Notifications__Smtp__From=tas@harbor-one.example  email sender address and name
Integration__Terminals__JED1__Url=...                        events to that terminal's systems
```

`GET /api/ops/status` lists each terminal's set-up: TOS, name, message providers and sender names, webhook. Sign-in
codes for drivers use the installation's settings, because a driver works at several terminals. An unknown TOS name
stops the server from starting, so a typo can't turn into failed bookings.

### Adding a terminal

In the portal, **Terminal set-up** lets system admins and operator admins add a terminal. It starts with the usual
move types, an in and an out lane and the usual booking rules. The same screen then works through the checklist.
Through the API:

```
POST /api/terminals                         {"code": "JED2", "name": "...", "terminalType": "Container", "timeZone": "Asia/Riyadh"}
GET  /api/terminals/{id}/setup              the checklist: which steps are done, and whether trucks can book
POST /api/terminals/{id}/setup/tos-test     asks the terminal's TOS a harmless question
```

The terminal's TOS and message settings stay in `.env` (`Terminals__<code>__…`), as above. The checklist shows when
the TOS is still the demo one.

### Country

`Country` (SA by default; AE, OM, BH, KW and QA are also included) decides how the installation reads mobile numbers
typed without a country code (in Saudi Arabia 05…, 5…, 9665…), how Arabic letters on plates are read, and the name of
the drivers' ID document. Numbers typed with `+` or `00` work from any country. These stay one per installation because
companies and drivers are shared by its terminals. Each terminal can have its own country for its public holidays
(shown on the slot board, never closing anything by themselves) and its currency. An unknown country stops the server
from starting.

### Languages

The portal is in English and Arabic; the drivers' app and the messages are also in Urdu and Hindi. A language pack
adds a language (any of the portal, the app and the messages) or corrects a built-in one, without a new version: a JSON
file per language in the `languages` folder of the install kit, made from its `template.json`, then
`docker compose restart app`. The folder's README explains the steps. A pack with a mistake (an unknown placeholder,
a code that doesn't match the file name) stops the start with the reason; `GET /api/ops/status` lists the packs in use.

### Branding

The installation and each operator have a name, a main colour and a logo (`/api/branding/installation`,
`/api/operators/{id}/branding`, or **Fleet & users → Operators** in the portal). An operator's shows for its terminals in
the portal, the drivers' app and messages; whatever it leaves out comes from the installation's, then Yardsyncs. The
name messages are signed with follows the same order; a terminal's own `Terminals__<code>__Brand` setting still comes
first.

An operator can also have its own web address (for example `tas.harbor-one.example`): sign-in there shows its look.
Point the name at the installation's server and list it in `TAS_EXTRA_DOMAINS` in `.env` (names separated by
spaces), so the HTTPS proxy gets a certificate for it. Colours must carry white text (contrast 4.5:1). Logos are PNG, JPEG or WebP up to 256 KB; SVG is refused
because it can carry scripts.

### Operators

When one company runs several of the installation's terminals, a system admin groups them under an operator
(**Fleet & users → Operators**, or the API below). The operator's admins (role `OperatorAdmin`) are terminal admins at
each of its terminals and see nothing of the others. Terminals that aren't in an operator work as before.

```
POST /api/operators                       {"code": "HARBOR1", "name": "Harbor One"}
PUT  /api/terminals/{id}/operator         {"operatorId": 1}      null takes the terminal out
POST /api/users                           {"role": "OperatorAdmin", "operatorId": 1, "email": ..., "fullName": ...}
PUT  /api/operators/{id}                  {"name": ..., "isActive": false}   signs its admins out
```

Operators group one company's terminals. Competing companies get separate installations (or separate cloud
instances), so their data never shares a database.

## Upgrade and roll back

```
./upgrade.sh 1.2.0       backup, new version, wait until ready (the database upgrades itself)
./upgrade.sh --rollback  the version before, with the database from the backup taken before the upgrade
```

Without internet, load the image file from the release page first (`docker load < yardsyncs-1.2.0.tar.gz`).

## Backups

A backup of the database every night (kept 14 days by default). Copy the `backups` folder off the server every day;
test a restore every few months.

## Monitoring

| What | Where |
|---|---|
| Alive | `GET /health/live` |
| Ready (database up and current) | `GET /health/ready` — 200 or 503 |
| Version, backlogs, warnings | `GET /api/ops/status` (as a terminal admin) |
| Logs | One JSON object per line on the container's output |

Alert when ready fails for 2 minutes, when messages wait longer than 15 minutes, when events to the terminal's
systems fail, when no backup appeared for 26 hours, and on log lines about locked-out accounts or reused sessions.

## Load

Every version is tested in CI with a morning rush (dispatchers booking the busy hours, trucks reporting positions,
the control room watching): about 110 requests a second on a small machine with 95 % of answers under 200 ms. Before
go-live a larger test runs against a copy of the terminal's installation with its own capacity plan.
