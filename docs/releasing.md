# Versions, environments and releases

## Versions

Yardsyncs versions are `MAJOR.MINOR.PATCH` (semantic versioning): PATCH for fixes, MINOR for new features that need
nothing from the customer, MAJOR when an upgrade needs work at the customer (a setting that must be set, an
integration that changes). The next version is in the `VERSION` file; release candidates are `1.0.0-rc.1`.

The running version is shown by `/health`, `/health/ready` and `/api/ops/status`.

## Images

GitHub builds one image per change and publishes it to `ghcr.io/mahmoudabdelhafiz-byte/yardsyncs`
(`.github/workflows/release.yml`):

| When | Image tags | Goes to |
|---|---|---|
| Every merge into `main` | `0.9.0-main.57` (VERSION + build number), `edge`, `sha-<commit>` | staging |
| A tag `v1.2.0` | `1.2.0`, `1.2`, `latest` (not for `-rc` versions) | production, after approval |

Each image carries its build provenance and a software bill of materials, and is scanned for known vulnerabilities
(Grype): problems with a fix available are listed on the run; a critical one stops a release. Pull requests scan the
image they build the same way; the server's .NET and web packages are checked too, and Dependabot proposes updates
every week.

A release also gets a GitHub release page with the install kit (`yardsyncs-deploy-1.2.0.tar.gz`, the `deploy/` folder
with the image already set), the image as a file for terminals without internet (`yardsyncs-1.2.0.tar.gz`) and their
checksums.

## Environments

| Environment | What runs there | Updated |
|---|---|---|
| Development | A developer's machine (`dotnet run`, demo data, test switches allowed) | By hand |
| CI | Every pull request: tests, the image with a fresh database, upgrade and roll back, load test | Automatically |
| Staging | Our server with demo data or a copy of a customer's settings; the `peak` load test runs here | Every merge into `main` |
| Production | Our cloud service, and each customer's on-premise server | A release, after approval |

Staging and production deploy over SSH by running `deploy/upgrade.sh` on the server. To switch them on (GitHub →
Settings → Environments, once per environment):

1. On the server: install with `deploy/` (see `deploy/README.md`), a user that may run Docker, and that user's
   public key in `~/.ssh/authorized_keys`. If the image is private, sign Docker in once with a token that may only
   read packages: `echo $TOKEN | docker login ghcr.io -u <github user> --password-stdin`.
2. Secrets of the environment: `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY` (the private key), `DEPLOY_KNOWN_HOSTS`
   (output of `ssh-keyscan <host>`), `DEPLOY_PATH` (the server's `deploy` folder).
3. Production: add **required reviewers**, so a release waits for someone to approve it.

Until the secrets exist the deploy jobs only publish the image and say so.

On-premise customers who don't allow us in run the upgrade themselves (below), after we tell them a release is out.

## Making a release

1. Make sure `main` is green and staging runs it without problems.
2. Set `VERSION` to the release (`1.2.0`), merge, then tag: `git tag v1.2.0 && git push origin v1.2.0`.
3. The release workflow publishes the image, the release page and, after approval, upgrades production.
4. Set `VERSION` to the next version (`1.3.0`) for the work that follows.

## Upgrading and rolling back a server

```bash
cd deploy
./upgrade.sh 1.2.0       # backup, new image, wait until ready (the app upgrades the database itself)
./upgrade.sh --status    # running version and the one before
./upgrade.sh --rollback  # the version before, with the database from the backup taken before the upgrade
```

Without internet: copy `yardsyncs-1.2.0.tar.gz` to the server, `docker load < yardsyncs-1.2.0.tar.gz`, then
`./upgrade.sh 1.2.0` (an image already on the server isn't downloaded).

Database upgrades only go forward, so going back means going back to the database as it was: a rollback loses what
was booked between the upgrade and the rollback. Decide quickly — within minutes, not hours — and upgrade outside the
busy hours.
