# Deploying Yardsyncs

One image holds the API and the web portal. It runs the same way at a terminal (on-premise) and in our cloud
service; what differs is where PostgreSQL lives and how HTTPS is provided.

## On one server (on-premise, or a cloud VM)

Needs: Linux with Docker (Compose v2), 4 CPU / 8 GB RAM / 100 GB disk for a mid-size terminal, ports 80 and 443.

```bash
git clone … && cd TAS/deploy
cp .env.example .env        # fill in every value (secrets stay in this file only)
docker compose up -d        # builds the image the first time; creates the database on first start
docker compose ps           # app, db, proxy, backup all "running"
curl -k https://$TAS_DOMAIN/health/ready
```

Sign in with `Bootstrap__AdminEmail` / `Bootstrap__AdminPassword`, change that password, create the terminal and its
users (they get an invitation email). Then set the email (SMTP) and SMS providers in `.env` and restart:
`docker compose up -d`.

**HTTPS.** `TLS=you@company.com` gets a public certificate from Let's Encrypt (the name must resolve to the server and
ports 80/443 be open). Inside a terminal network without internet, `TLS=internal`: Caddy issues its own certificate;
install its root (`docker compose cp proxy:/data/caddy/pki/authorities/local/root.crt .`) on the terminal's PCs and
the gate tablets. To use the company's own certificate instead, mount it into the proxy and change `tls` in
`Caddyfile` to `tls /certs/cert.pem /certs/key.pem`.

**The driver app** needs the server reachable from the phones (mobile data), so on-premise installations publish
`TAS_DOMAIN` to the internet through the terminal's firewall — only ports 80/443 of the proxy.

## Upgrading

```bash
git pull                     # or set YARDSYNCS_IMAGE to the new version
docker compose build app     # when building here
docker compose up -d
```

The app creates or upgrades the database itself when it starts (several instances wait for each other). To run the
upgrade on its own, e.g. before switching traffic: `docker compose run --rm app migrate`.
`/health/ready` answers 503 until the database is at the version the app needs.

## Backups and restore

The `backup` service writes `backups/tas-YYYYMMDD-HHMMSS.dump` every night at `BACKUP_AT` and keeps `BACKUP_DAYS`
days. **Copy `backups/` off the server** (company backup system, another site); a backup on the same disk does not
survive the disk. A backup right now: `docker compose run --rm backup now`.

Restore (stops the app, replaces the database):

```bash
docker compose stop app
docker compose exec db dropdb -U tas tas
docker compose exec db createdb -U tas tas
docker compose exec -T db pg_restore -U tas -d tas --no-owner < backups/tas-20261016-013000.dump
docker compose start app
```

Test a restore every few months on a spare machine — an untested backup is a hope, not a backup.

## Cloud service (our hosting)

The same image on a container platform (Azure Container Apps, AWS ECS / App Runner, Kubernetes) with a managed
PostgreSQL 16 that has PostGIS (Azure Database for PostgreSQL flexible server, Amazon RDS): set
`ConnectionStrings__Tas` and the settings of `.env.example` as the platform's secrets, `Proxy__TrustForwardedHeaders=true`
behind its load balancer, and use the platform's managed certificate. Point the load balancer's health probe at
`/health/ready`, the liveness probe at `/health/live`. Backups: the managed database's point-in-time restore.

One deployment can serve several terminals (each a terminal in the portal); a customer who wants its data apart gets
its own deployment and database.

## Monitoring

| What | Where |
|---|---|
| Process alive | `GET /health/live` (restart when it fails) |
| Ready for traffic: database reachable and up to date | `GET /health/ready` (200 / 503, JSON with the details) |
| Version, database, message and event backlogs | `GET /api/ops/status` (system and terminal admins) |
| Logs | stdout, one JSON object per line (`docker compose logs app`); ship them to the log system in use |

Alert when: `/health/ready` fails for 2 minutes; `notifications.queued` keeps growing or `oldestQueuedMinutes` > 15
(SMS / email provider down); `integrationEvents.failedLast24h` > 0 (the terminal's system refuses events); no new
file in `backups/` for 26 hours.
