# Deploying FileVault

How to run FileVault for real: behind HTTPS, with the right secrets, backed up, and with the
security settings that matter. The quick start in the [README](../README.md#run-everything-with-docker)
gets it running on `localhost`; this guide picks up from there.

## Deploy to a VPS (released images)

The quickest way to a real server: no source code, no build. A release publishes three images to
the GitHub Container Registry (`ghcr.io/chrislpjones/filevault-api`, `-frontend` and `-db-init`),
and `deploy/docker-compose.yml` runs them with SQL Server. Sections 1 to 9 below still apply
(HTTPS, secrets, backups, keys); this section is the short path.

**You need:** an amd64 server (the images are not built for ARM) with at least 2 GB RAM, Docker with
Compose 2.24 or later, two hostnames on one registrable domain (for example `files.example.com` and
`api.files.example.com`, see section 1) pointing at it, and a firewall that allows only 22, 80 and 443.

1. **Fetch the compose file and settings** for the release you want (here `v1.0.0`; `<tag>` is the
   release tag):

   ```bash
   mkdir filevault && cd filevault
   curl -fsSLO https://raw.githubusercontent.com/ChrisLPJones/FileVault/v1.0.0/deploy/docker-compose.yml
   curl -fsSL https://raw.githubusercontent.com/ChrisLPJones/FileVault/v1.0.0/.env.example -o .env
   chmod 600 .env
   ```

2. **Fill in `.env`** (section 2): the four secrets, plus for a public server
   `FRONTEND_URL=https://files.example.com`, `API_URL=https://api.files.example.com`,
   `BEHIND_HTTPS_PROXY=true`, `INITIAL_ADMIN_EMAIL` (section 7) and the `SMTP_*` settings.
   `FILEVAULT_VERSION` picks the release, e.g. `FILEVAULT_VERSION=1.0.0` (without the `v`). Without
   it you get `latest`, the newest non-pre-release; pinning means a server only changes when you
   decide. Optional `API_PORT` and `FRONTEND_PORT` change the loopback ports (3000 and 5173).

3. **Registry access.** If the packages are public, nothing to do. New GHCR packages start out
   private: the owner makes each one public once, in GitHub under the package's settings. While they
   are private, log in on the server with a personal access token that has only the `read:packages`
   scope: `echo "$TOKEN" | docker login ghcr.io -u <github-user> --password-stdin`.

4. **Start it:** `docker compose up -d`. The API and the frontend listen on `127.0.0.1` only, so
   nothing is reachable until you add the reverse proxy: use the Caddy or nginx examples in
   section 1 (`reverse_proxy 127.0.0.1:5173` and `127.0.0.1:3000`), then register the first
   administrator (section 7).

5. **Update:** set the new `FILEVAULT_VERSION` (or keep `latest`), then
   `docker compose pull && docker compose up -d`. `db-init` applies schema changes first; `init.sql`
   only adds, so the old version still runs against the upgraded database. **Roll back** by setting
   the previous version and running the same two commands. Back up first (section 3).

Changing `API_URL` later needs no new image: edit `.env` and run `docker compose up -d`.

**Releasing (maintainers).** Push a tag like `v1.2.3` to the repository. CI runs all checks and then
publishes the three images tagged `1.2.3` and `latest`; a pre-release tag such as `v1.2.3-rc.1`
publishes only `1.2.3-rc.1`. After the first release, set each package public if you want servers to
pull without a login.

The source stack (`docker-compose.yml` in the repository) builds the same images locally. After
pulling schema changes there, use `docker compose up --build -d` (the `db-init` image carries
`init.sql`, so without `--build` it would be stale).

## 1. Serve it over HTTPS

FileVault's containers speak plain HTTP. Put a reverse proxy in front of them that terminates TLS,
and don't expose the containers' ports to the internet.

The browser talks to two origins: the frontend (nginx, container port 80) and the API (container
port 8080). The simplest setup gives each its own hostname, for example `files.example.com` and
`api.files.example.com`. Keep them on the same registrable domain: the refresh-token cookie is
`SameSite=Strict`, so it is only sent when the frontend and API are the same *site*. The browser
keeps the access token in memory only and gets a new one from that cookie on every page load, so
on different sites users are signed out each time they reload or open a new tab.

In `.env`:

```bash
FRONTEND_URL=https://files.example.com        # CORS: the only origin allowed to call the API
API_URL=https://api.files.example.com         # read by the frontend container at start-up (config.js and its CSP)
BEHIND_HTTPS_PROXY=true                       # see below
```

`API_URL` is read when the frontend container starts, so after changing it `docker compose up -d` is enough (no rebuild). It must be empty or an `http(s)://host[:port][/path]` URL (letters, digits, `.`, `-`; no query string, spaces or quotes); anything else stops the container with a message in its log.

`BEHIND_HTTPS_PROXY=true` makes the API:

- trust the proxy's `X-Forwarded-For` and `X-Forwarded-Proto` headers, so rate limits and the
  sessions list see the real client IP, and requests count as HTTPS;
- always mark the refresh-token cookie `Secure`.

Only set it when the API can't be reached except through the proxy. Otherwise anyone could send
their own `X-Forwarded-For` header and dodge the per-IP rate limits. With the proxy on the same
host, publish the containers on the loopback interface only, e.g. in `docker-compose.override.yml`
(`!override` needs Docker Compose 2.24 or later):

```yaml
services:
  api:
    ports: !override
      - "127.0.0.1:3000:8080"
  frontend:
    ports: !override
      - "127.0.0.1:5173:80"
```

(The settings behind it are `ForwardedHeaders__Enabled` and `Jwt__SecureRefreshCookie`. To trust
only specific proxy addresses, set `ForwardedHeaders__KnownProxies__0=10.0.0.5` and so on.)

### Caddy

Caddy gets and renews certificates automatically.

```caddyfile
files.example.com {
    reverse_proxy 127.0.0.1:5173
}

api.files.example.com {
    # Allow large uploads (the API's own limit is Storage:MaxUploadBytes, 100 MB by default)
    request_body {
        max_size 110MB
    }
    reverse_proxy 127.0.0.1:3000
}
```

Caddy sets `X-Forwarded-For` and `X-Forwarded-Proto` itself.

### nginx

```nginx
server {
    listen 443 ssl;
    http2 on;
    server_name files.example.com;
    ssl_certificate     /etc/letsencrypt/live/files.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/files.example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:5173;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}

server {
    listen 443 ssl;
    http2 on;
    server_name api.files.example.com;
    ssl_certificate     /etc/letsencrypt/live/api.files.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/api.files.example.com/privkey.pem;

    client_max_body_size 110m;      # uploads; the API enforces its own limit
    proxy_request_buffering off;    # stream uploads straight through

    location / {
        proxy_pass http://127.0.0.1:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}

# Redirect plain HTTP to HTTPS
server {
    listen 80;
    server_name files.example.com api.files.example.com;
    return 301 https://$host$request_uri;
}
```

## 2. Secrets and settings

Set these in `.env` next to `docker-compose.yml` (it's gitignored). Compose refuses to start
without the first four.

| Variable | What it is |
|---|---|
| `MSSQL_SA_PASSWORD` | SQL Server `sa` password. Only applied when the database volume is first created. Only SQL Server and the `db-init` setup container get it; the API does not |
| `MSSQL_APP_PASSWORD` | Password of the API's own database login, `filevault_app` (see "Database login" below). Different from the `sa` password; `openssl rand -base64 24`. No quotes, semicolons, `$` or spaces, and it must meet SQL Server's password policy. Changing it and running `docker compose up -d` changes the login's password |
| `JWT_KEY` | Signs access tokens. At least 32 bytes: `openssl rand -base64 48` |
| `ENCRYPTION_MASTER_KEY` | Encrypts every file's key, the two-factor secrets and share-link secrets. Exactly 32 bytes, base64: `openssl rand -base64 32`. The API refuses to start with a key other than the one the database was encrypted with |
| `ENCRYPTION_MASTER_KEY_NEW` | Only while rotating the master key (section 4): the new key. Remove it afterwards |
| `FRONTEND_URL` | Where the browser loads the app; the only origin the API accepts calls from |
| `API_URL` | Where the browser reaches the API. Read at container start: `docker compose up -d` after changing it |
| `FILEVAULT_VERSION` | Server compose file only (`deploy/docker-compose.yml`): the image version to run (default `latest`); pin it, e.g. `1.0.0` |
| `API_PORT`, `FRONTEND_PORT` | Server compose file only: loopback ports for the API and frontend (3000 and 5173) |
| `BEHIND_HTTPS_PROXY` | `true` behind an HTTPS reverse proxy (section 1) |
| `SWAGGER_ENABLED` | `true` to serve the API docs (section 5). Off by default |
| `FILEVAULT_MODE` | Optional: `self-hosted` (default) or `hosted` (section 9). Anything else stops the API starting |
| `HOSTED_CONTACT_EMAIL` | Optional, hosted mode only: the address users are told to email to ask for a permanent account |
| `STORAGE_MAX_DISK_USE_PERCENT` | Optional: uploads are refused once the drive holding the file storage is this percent full (1-99, default 80, `0` = off). See "Disk limit" below |
| `INITIAL_ADMIN_EMAIL` | Optional, recommended on a public install: the one email that can become the first administrator (section 7) |
| `GEOIPUPDATE_ACCOUNT_ID`, `GEOIPUPDATE_LICENSE_KEY` | Optional: MaxMind credentials for the `geoip` profile, which downloads the country database (section 8) |

Other API settings can be passed as environment variables on the `api` service using
`Section__Key` names, for example `Storage__DefaultQuotaBytes`, `Storage__MaxUploadBytes`,
`RateLimiting__auth__PermitLimit` or `TwoFactor__Issuer` (the name shown in authenticator apps).
See `Backend/appsettings.json` for the full list and defaults.

**Database login.** The API does not connect as `sa`. On every `docker compose up` the `db-init`
container, which does have the `sa` password, runs `init.sql` and then `Backend/Docker/db/app-login.sql`,
which creates (or updates) the login `filevault_app` with the password from `MSSQL_APP_PASSWORD`. That login
can read and write the data in the `dbo` schema and nothing else: it cannot create, alter or drop tables
or databases, is not `db_owner` or `sysadmin`, and cannot read other databases. So a SQL injection or
code-execution bug in the API cannot be used to take over SQL Server (`xp_cmdshell`, other databases,
dropping the database). The API can still read and write all FileVault data, which it needs to. The
login also holds `VIEW SERVER PERFORMANCE STATE` (SQL Server 2022), which lets `rotate-master-key` see
other connections to the database and refuse to rotate while any are open; it grants no data access.
The login's password never expires. Database maintenance by hand (section 3, 4) still uses `sa` through
the `sqlserver` container. The API warns at start if it finds its login is `sysadmin` or `db_owner`
(as in local development, which uses `sa`).

*Upgrading to the least-privilege login:* `docker compose up` stops with "Set MSSQL_APP_PASSWORD in
`.env`" until you add one. Generate it straight into `.env` so it doesn't pass through your shell
history, then rebuild:

```bash
printf 'MSSQL_APP_PASSWORD=%s\n' "$(openssl rand -base64 24 | tr -d '/+=')Aa1" >> .env
docker compose up --build -d
```

No data is touched; `db-init` creates the login before the API starts, and the schema and files stay
as they are. To go back, use the previous version's `docker-compose.yml` (it connects as `sa`); the extra
login is harmless.

**Disk limit.** The API refuses uploads (HTTP 507, "The server's storage is almost full") that would
take the drive holding `StorageRoot` past `STORAGE_MAX_DISK_USE_PERCENT` (`Storage:MaxDiskUsePercent`)
of its size, so a full disk can't stop writes or take down SQL Server if it shares the disk. It
applies in both modes and to every account, administrators included. The check counts the drive's
used space, the bytes of chunked uploads still in progress (all accounts) and the new file; copying
files writes nothing and is not checked. It is soft: several single-request uploads (at most 100 MB
each) arriving at once can overshoot slightly. If the drive can't be read the upload is allowed and
a warning is logged. The Admin page's Disk tile shows the limit, and "Uploads are paused" while the
drive is over it. In Docker the percentage is of the drive behind the `filevault_file_storage`
volume (normally the one holding `/var/lib/docker`, shared with the database volume); if Docker's
data root is on another disk, the limit follows that disk.

*Upgrading:* the default is 80 for self-hosted installs too. If your drive is already more than 80 %
full, uploads stop after the upgrade (the API logs a warning at start) until you free space or set
`STORAGE_MAX_DISK_USE_PERCENT` higher, or to `0` to keep the old behaviour.

Changing the email address from Settings is limited per account to `EmailChange__MaxPerDay`
confirmation links per 24 hours (default 5) and `EmailChange__ResendCooldownSeconds` between them
(default 60), checked and counted in one step per account so parallel requests can't exceed them, on top of the per-IP `RateLimiting__email__PermitLimit`. The link goes to the new
address and must be opened while signed in as that account (it points at `App:FrontendUrl`, like the
other email links); the old address stays the login until then. No database change is needed.

`Auth__UserStateCacheSeconds` (default 30) is how long the API caches each user's sign-in state when
checking access tokens. When an administrator sets a user's password or suspends an account, that user's current access
token stops working immediately; changes made on another API instance or directly in the database can take up
to this many seconds.

Keep the master key somewhere other than the server too, such as a password manager.

## 3. Backups

Everything lives in two Docker volumes:

- `filevault_sql_data`: the database (accounts, file metadata, each file's wrapped key)
- `filevault_file_storage`: the encrypted files

**Back up both volumes together, and keep a copy of `ENCRYPTION_MASTER_KEY`.** The three only work
as a set: the files can't be decrypted without their keys from the database, and those keys can't
be unwrapped without the master key. A database backup from a different moment than the files
backup leaves files without keys (lost) or keys without files. Lose the master key and every file,
every two-factor secret and every share-link secret is unreadable. A backup is only readable with the
master key that was in use when it was made, so after a rotation (section 4) keep the old key with
the old backups.

### Scheduled off-site backups (recommended)

`deploy/backup.sh` takes a consistent backup while the stack keeps running and sends it, encrypted on
your server before it leaves, to a [restic](https://restic.net) repository. restic runs as a Docker
image (`restic/restic`, pinned in the script), so nothing is installed on the host. Each run:

1. makes a verified full database backup (`BACKUP DATABASE ... WITH CHECKSUM`, then `RESTORE VERIFYONLY`)
   into the database volume, checking free space first;
2. backs up that file and the encrypted files in one restic snapshot (database first, then files: every
   database row then has its file; a file newer than the database backup is only an unused leftover);
3. keeps 7 daily, 4 weekly and 6 monthly snapshots and deletes the rest;
4. on Sundays reads back 5% of the repository to check it;
5. pings [healthchecks.io](https://healthchecks.io) (optional) so you hear about a failed run, and also about
   a run that never happened (cron stopped, server down).

The database uses SIMPLE recovery (set by `init.sql`), so the restore points are the daily backups; there
is no point-in-time restore and no log to back up. Only `SecureVaultDb` is saved: the logins are recreated
on restore. The script never reads `.env`: the master key, the database passwords and the JWT key are not
in the backup.

**Set up** (on the server, as the user that runs Docker):

1. Create a storage bucket, e.g. Backblaze B2: a private bucket, and an application key limited to that
   bucket. Keep it to read/write on that bucket. Be aware that a lifecycle rule such as "keep prior versions
   for 30 days" does NOT protect the history from someone who holds the server's key (or root on the server):
   they can delete old versions too. Real protection needs B2 Object Lock (immutable retention), or a key
   without delete rights with `restic forget --prune` run only from a trusted machine. Neither is set up by
   default; add one if you need to survive a compromised server.
2. `mkdir -p ~/filevault && cd ~/filevault`, then create the restic password file and the settings:
   ```bash
   (umask 077; openssl rand -base64 32 > restic-password)
   curl -fsSLO https://raw.githubusercontent.com/ChrisLPJones/FileVault/<tag>/deploy/backup.sh
   curl -fsSL  https://raw.githubusercontent.com/ChrisLPJones/FileVault/<tag>/deploy/backup.env.example -o backup.env
   chmod 700 backup.sh && chmod 600 backup.env     # then fill in backup.env
   ```
   (or use the copies in your checkout's `deploy/`). `backup.env` holds the repository, the bucket key and
   the optional healthchecks URL; see the comments in `backup.env.example`. If you did not name the
   Compose project, it is `filevault` (the `name:` in the compose file).
3. First run, which also creates the repository: `./backup.sh --init`. Read the output: it prints the
   row counts and the key tag, and ends with `backup OK`.
4. In healthchecks.io create a check with period 1 day and grace time 2 hours, put its ping URL in
   `backup.env` as `HC_PING_URL`, and add an email or other alert.
5. Schedule it with cron (`crontab -e`), daily at 03:30 server time:
   `30 3 * * * /home/deploy/filevault/backup.sh > /dev/null 2>&1`. The script writes its own log
   (`backup.log` next to `backup.env`, rotated at 1 MB) and refuses to run twice at once.
6. Put these in your password manager now: **`ENCRYPTION_MASTER_KEY`**, the **restic password** and the
   bucket account login. Without the restic password the repository cannot be opened; without the master key
   the restored data cannot be read. Nothing else in `.env` is needed to restore (the database passwords and
   JWT key can be new).

Every snapshot is tagged `mk-<12 hex characters>`: the start of the fingerprint of the master key that
opens it (not the key). `restic snapshots` shows it, so after a rotation you can tell which key a snapshot
needs. After rotating (section 4) the new snapshots carry the new tag; keep the old key until the last
snapshot with the old tag has been pruned (about six months with the retention above). Take a backup right
before rotating. Deleted accounts also stay in older snapshots until those expire.

Copy `deploy/restore.sh` too if you want it on the server; you can equally run it from a checkout on any
machine that has Docker and the compose files.

### Restoring, and the test restore

`deploy/restore.sh` restores a snapshot into a **new, separate** Compose project; it refuses the production
project (`filevault`) and a project whose volumes already exist, and needs the project name explicitly:

```bash
# restore.env: a .env for the copy. ENCRYPTION_MASTER_KEY = the key the snapshot's mk- tag belongs to,
# typed from your password manager; the other secrets can be new. chmod 600.
# ports.yml: publish the copy on this machine only (restore.sh refuses any other binding)
#   services:
#     api:      { ports: !override ["127.0.0.1:3101:8080"] }
#     frontend: { ports: !override ["127.0.0.1:5273:80"] }
deploy/restore.sh --project fvrestore --env-file restore.env --backup-env ~/filevault/backup.env \
    --compose-file docker-compose.yml --compose-file ports.yml [--snapshot <id>]
```

Before restoring anything the script checks that every published port of the new stack is bound to
`127.0.0.1`; otherwise it removes the empty stack and stops. It also fails if the API restarted or logged
that the master key does not match.

It creates the stack's empty volumes, restores the files and the database backup into them, restores
the database, starts the stack (the schema setup re-links the API's database login), waits to see that
the API accepts the key, and prints the row counts and the files missing from disk or not in the database.
`--verify-only` repeats just that last check. The API refuses to start with the wrong master key.
Use the same or a newer FileVault version than the one that made the snapshot, never an older one.

Do the **test restore** once after the first backup and then every few months, on another machine and
not on the production server, with the key from your password manager (that proves the stored copy, not
the server's `.env`). Give the copy no mail settings (so it cannot email anyone), keep
`FILEVAULT_MODE=self-hosted`, and publish its ports on `127.0.0.1` only (the `ports.yml` above; enforced). Then: compare the printed
counts with the snapshot's tags, sign in as one of your accounts, download a file and compare its checksum
with the original. Remove the copy with `docker compose -p fvrestore down -v`, and delete `restore.env`.

To restore for real after a loss: set up the server and `.env` (with the right master key), restore with
a new project name as above, check it, then stop that copy, and start the production stack on the restored
volumes (or run the same steps against the production name by hand). The scripts deliberately never
overwrite the production volumes.

The compose files run SQL Server as the free Express edition (`MSSQL_PID=Express`, 10 GB database limit); the Developer edition the image defaults to is not licensed for production.

Upgrading an existing install to Express (the compose files now set `MSSQL_PID=Express`):

1. Run `deploy/backup.sh` first.
2. Check the database is well under 10 GB and uses no Enterprise-only feature (the second query must return no rows):
   `docker compose exec sqlserver sh -c '/opt/mssql-tools18/bin/sqlcmd -C -U sa -P "$MSSQL_SA_PASSWORD" -d SecureVaultDb -Q "SELECT SUM(size)*8/1024 AS MB FROM sys.database_files; SELECT * FROM sys.dm_db_persisted_sku_features"'`
3. `docker compose up -d` (recreates the SQL Server container).
4. Check `SELECT SERVERPROPERTY('Edition')` says Express and `SELECT state_desc FROM sys.databases WHERE name = 'SecureVaultDb'` says ONLINE.
5. To roll back, remove the `MSSQL_PID` line from the compose file and run `docker compose up -d` again (the data volume is not touched).

Maintainers: `shellcheck` runs on both scripts in CI. The full round trip (seed, back up to a local
directory repository, restore into a second project, compare a downloaded file) is manual: set
`RESTIC_REPOSITORY=/some/dir` in a throwaway `backup.env` and use throwaway Compose projects.

### Simple alternative: cold copy

If you do not want scheduled backups, this stops the API and the database briefly (SQL Server's data
files can't be copied safely while it's running):

```bash
docker compose stop api sqlserver
docker run --rm -v filevault_sql_data:/data -v "$PWD/backup:/backup" alpine@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc \
    tar czf /backup/sql_data-$(date +%F).tar.gz -C /data .
docker run --rm -v filevault_file_storage:/data -v "$PWD/backup:/backup" alpine@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc \
    tar czf /backup/file_storage-$(date +%F).tar.gz -C /data .
docker compose start sqlserver api
```

Restore by extracting both archives into empty volumes of the same names before starting the stack,
with the same `ENCRYPTION_MASTER_KEY` in `.env`.

## 4. Rotating keys

### The JWT key

Change `JWT_KEY` in `.env` and run `docker compose up -d`. Every access token signed with the old key
stops working at once. Refresh tokens are random values checked against the database, not signed,
so an open browser tab quietly gets a new access token on its next request and stays logged in.

To log **everyone** out as well (for example after a suspected leak), also revoke every refresh token:

```bash
docker compose exec sqlserver sh -c '/opt/mssql-tools18/bin/sqlcmd -C -U sa -P "$MSSQL_SA_PASSWORD" \
    -d SecureVaultDb -Q "UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME() WHERE RevokedAt IS NULL"'
```

Users then log in again (with their two-factor code if they use one). Their files are unaffected.

Don't change `ENCRYPTION_MASTER_KEY` by hand: use the rotation command below, or every stored file
becomes unreadable. (The API refuses to start with a key other than the one the database is
encrypted with, so a wrong edit stops the API instead of corrupting data.)

### Rotating the master key

`rotate-master-key` re-wraps everything stored under `ENCRYPTION_MASTER_KEY` with a new key in one
database transaction: the per-file data keys, thumbnail and avatar keys, in-progress upload keys,
two-factor secrets and share-link tokens and passwords. File contents are not re-encrypted (only their
keys change), so it takes seconds, not hours. Either everything is rewritten and verified, or nothing
changes; a crash or an error part-way rolls back, and running the command again is safe. If it says the
outcome of the commit is unknown, keep both keys and run it again: it reports whether the rotation went through.

Upgrading from a version without this command needs nothing special: `docker compose up -d` adds the
`MasterKeyState` table and the first start records which key the database uses.

1. **Back up first** (section 3: run `deploy/backup.sh` so a snapshot made with the old key exists), including the current `ENCRYPTION_MASTER_KEY`.
2. Generate the new key straight into `.env`, so it doesn't pass through your shell history:
   `printf 'ENCRYPTION_MASTER_KEY_NEW=%s\n' "$(openssl rand -base64 32)" >> .env`
3. If you updated FileVault, `docker compose build api` so the command exists, then
   `docker compose stop api`. The command refuses to run while another connection is open to the
   database (it lists them by program name); it can only see that, so stop the API yourself first,
   especially on the first rotation after upgrading from a version without this command.
4. Check first: `docker compose run --rm api rotate-master-key --dry-run`. It reads and re-wraps
   everything, verifies it and rolls back, printing counts per column and the row IDs of anything that
   does not decrypt. It prints a short ID of each key (never the key).
5. Rotate: `docker compose run --rm api rotate-master-key`
6. In `.env` set `ENCRYPTION_MASTER_KEY` to the new value and delete the `ENCRYPTION_MASTER_KEY_NEW`
   line, then `docker compose up -d`. Back up the new key; keep the old one only for backups made
   before the rotation.

If the API will not start afterwards with a message that the key does not match, `.env` still has the
old key. A key is only ever read from `.env` / the environment, never from a command argument (an
unknown argument is rejected without printing it). While the command runs, `docker inspect` on its
container shows both keys, as it does for the API container with the current key.

Options (flags, not keys):

- `--dry-run`: change nothing. Without `ENCRYPTION_MASTER_KEY_NEW` it uses a throwaway key, which is
  enough to check the current key and every stored value.
- `--skip-unreadable`: by default a value that does not decrypt with the current key (damaged data)
  stops the rotation and is listed by table and row ID. This flag rotates the rest and leaves those
  rows as they are (they were already unusable).
- `--reset-recovery-codes`: see below.

Messages: *the API is still running* or *other connections are open* means stop the API (`docker compose stop api`) and anything else using the database. *Not the key this
database is encrypted with* means `ENCRYPTION_MASTER_KEY` is wrong (nothing was changed).
*Already encrypted with the new key* means an earlier run finished; only `.env` needs updating.

**Two-factor recovery codes.** Only a keyed hash of each code is stored, so they cannot be re-hashed.
By default the hashing key is carried over (stored protected under the new master key), so every
existing recovery code keeps working. If you rotate because the key and a copy of the database may
both have leaked, add `--reset-recovery-codes`: all recovery codes are deleted and new ones use the
new key. Users with two-factor sign-in then generate new codes in Settings (while they still have
their authenticator), so tell them first.

**What rotation does not protect.** It stops the *old key* opening the live database from now on, with one
exception: under the default carry-over the old key still derives the recovery-code hashing key, which
keeps hashing existing codes and codes generated later, until you rotate with `--reset-recovery-codes`. If
the database and the old key leaked together, the secrets that did not change value are exposed:
files' data keys (files are not re-encrypted), authenticator secrets (users should re-enrol two-factor)
and share-link tokens (owners should revoke and recreate links). Backups made before the rotation
still need the old key.

## 5. API docs (Swagger)

The interactive docs at `/swagger` are served when the API runs in Development (`dotnet run`), and
otherwise only when `Swagger:Enabled` is true. The Docker stack runs in Production, so they're off.
To turn them on, add `SWAGGER_ENABLED=true` to `.env` and run `docker compose up -d`; remove it (or
set it to `false`) to turn them off again. They describe the API but don't bypass its
authentication, so leaving them off in production is about not advertising the API surface.

## 6. Security headers

**API** (every response, set in `Backend/Services/SecurityHeaders.cs`):

- `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`
- `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`: the API only returns data,
  so a response opened directly can't run anything or be framed (Swagger UI only gets the
  `frame-ancestors` rule, as it's a real page)
- `Cache-Control: no-store` on `/user/*` (login, tokens, profile, sessions, two-factor), unless the
  endpoint sets its own caching
- The refresh cookie is `HttpOnly`, `SameSite=Strict`, limited to `/user`, and `Secure` on HTTPS
  (section 1)

**Frontend** (nginx, `Frontend/nginx/`):

- `Content-Security-Policy`: scripts, styles and fonts from the app's own origin only (no inline
  scripts; the font is bundled rather than loaded from Google Fonts); `connect-src` and `img-src`
  add the API origin from `API_URL`; `blob:` and `data:` images (previews, profile pictures, the
  two-factor QR code); `blob:` media and frames (video/audio and PDF previews); `form-action`
  and `frame-src` also allow the API origin, for the download form on public share pages (it
  posts into a hidden frame); `object-src 'none'`, `base-uri 'self'`, `frame-ancestors 'none'`
- `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`,
  and a `Permissions-Policy` that turns off camera, microphone, geolocation, payment and USB
- `Strict-Transport-Security: max-age=31536000`, only when the request came through the proxy over
  HTTPS (`X-Forwarded-Proto: https`). Browsers ignore HSTS over plain HTTP, and sending it on a
  `localhost` setup would be pointless. Once it's been sent, browsers refuse plain HTTP for that
  host for a year, so only put FileVault behind HTTPS you intend to keep. Add `includeSubDomains`
  or `preload` in your proxy only if every subdomain is HTTPS too.

The nginx config is a template (`/etc/nginx/templates/default.conf.template`); at start-up the
container validates `API_URL`, works out the API origin from it and writes the real config, plus
`/config.js` (the API URL for the app; served `no-store`, same origin so the CSP covers it). To check the headers:

```bash
curl -sI https://files.example.com/ | grep -iE "content-security|strict-transport|x-frame|referrer|permissions"
curl -sI https://api.files.example.com/ping | grep -iE "content-security|x-frame|referrer"
```

## 7. The first administrator

The admin page (`/admin`) shows every account's storage use and quota. Who can open it is
decided in the app, not in `.env`.

**Without `INITIAL_ADMIN_EMAIL`**, the first account registered on a new install becomes the
administrator. On a public server, register straight after the first start, before anyone else can.

**With `INITIAL_ADMIN_EMAIL` set** (recommended), nobody is an administrator at registration. The
account with that email becomes one only once its mailbox is proven, by either:

- registering, then opening the confirmation link in the email. Opening the link for a new sign-up
  also clears that account's password, so then use "Forgot password?" to choose one; or
- using "Forgot password?" directly: the reset sets your password, confirms the email and
  promotes the account.

It also happens at API start for an account with that email that is already confirmed. An account
whose email was changed to this address can never become the first administrator, by the link, at
start or by a password reset. (Asking to change an email to this address does not occupy it: the new
address is only pending until confirmed, so the owner can still register it.) Emails are compared exactly (look-alike characters don't match).

Email delivery needs the `SMTP_*` settings (or, self-hosted only, find the links in `docker compose logs api`).

**If someone registered your address first** (a new sign-up), use "Forgot password?" for it. The
reset gives you the account and the squatter's password stops working. If someone instead changed
their existing account's email to your address, use a different address for `INITIAL_ADMIN_EMAIL`,
or have an operator remove that account.

Use a plain-ASCII, lower-case address for `INITIAL_ADMIN_EMAIL`. Someone registering a look-alike
address first (for example with `ß` instead of `ss`) can't become administrator, but can stop you
registering that address. Fix it by choosing a different `INITIAL_ADMIN_EMAIL` or removing that
account.

Once there is an administrator, they can create further administrator accounts on the Admin page
(Administrator tick box when creating an account); admin rights can't be changed afterwards in the
app. Administrator accounts are always permanent (never removed for inactivity) and have no row
actions on the Admin page. There must always be at least one administrator: removing the
last one, or the last administrator deleting their own account, is refused.

Administrators can also create accounts (active immediately, email already confirmed), set a user's
password (signs them out everywhere), suspend and unsuspend accounts, mark accounts permanent, and
delete accounts. A suspended administrator doesn't count as an administrator until unsuspended. Each of these
writes an audit line to the API log under the `Backend.AdminAudit` category, using account ids only
(no emails or passwords), so you can watch that category for admin activity.

**Upgrading an existing install**

- `ADMIN_EMAILS` (`Admin:Emails`) no longer makes anyone an administrator. If it is still set, the
  API logs a warning at start and ignores it. Existing administrators keep their rights.
- If the install has accounts but no administrator, at API start the oldest confirmed account
  becomes one (or, with `INITIAL_ADMIN_EMAIL` set, only that account). Accounts from before this
  version are trusted, so before (or right after) the first start of the new version, check the user
  list. If in doubt, set the administrator yourself: leave `INITIAL_ADMIN_EMAIL` unset so the oldest
  confirmed account is promoted, or set `IsAdmin` in the database.
- An account stored with capital letters by an old version won't match `INITIAL_ADMIN_EMAIL`. For
  that install leave the setting unset, or lower-case the address in the database.
- The upgrade adds `Users.EmailChanged`, `Users.TokensValidAfter`, `Users.IsPermanent` and `Users.SuspendedAt` columns;
  re-running `init.sql` (`docker compose up --build -d`) applies them.
- Admin membership changes take a short database lock. If it can't be had in time the API answers
  `503` with a `Retry-After` header; retrying a moment later works.

## 8. Last login location (GeoIP)

The Admin page shows each user's last login IP address and country. Only the latest login address
is stored (`Users.LastLoginIp`), and it is recorded when a session is issued. The country is
looked up when the page loads, in a local MaxMind GeoLite2 Country database, and is never stored.
No address is sent to MaxMind or anyone else. Without the database the country shows "Unknown".

**Set it up**

1. Create a free [MaxMind](https://www.maxmind.com) account and generate a licence key.
2. Put `GEOIPUPDATE_ACCOUNT_ID` and `GEOIPUPDATE_LICENSE_KEY` in `.env`.
3. Start the updater: `docker compose --profile geoip up -d`. The `geoipupdate` service downloads
   `GeoLite2-Country.mmdb` into the `geoip_data` volume and refreshes it every 72 hours. The API
   mounts the volume read-only (`GeoIp__DatabasePath` is `/data/geoip/GeoLite2-Country.mmdb`) and
   picks up the new file when it changes.

Without a MaxMind account, copy a `GeoLite2-Country.mmdb` into the `geoip_data` volume yourself
(Compose names it `filevault_geoip_data`).

**MaxMind's licence**

- Don't commit the `.mmdb` file to git or bake it into a Docker image; the volume keeps it out of both.
- Keep it updated; MaxMind's terms require using the current version. The `geoip` profile does this.
- The README carries the required GeoLite2 attribution.

**Behind a proxy**

- Without `BEHIND_HTTPS_PROXY=true`, behind a reverse proxy every user shows the proxy's IP.
- With it, the API trusts `X-Forwarded-For`, so the API port must not be reachable except through
  the proxy (section 1). Otherwise anyone can send their own header and fake the address shown.
  Setting `ForwardedHeaders__KnownProxies__0` to your proxy's address (section 1) narrows this further.

**Privacy**

Storing IP addresses is personal data processing. If you run a public instance, mention the
last-login IP logging in your privacy notice. Only the latest login address is kept for the admin page, and only admins can see it; active sessions also keep their own address (users see it under Active sessions).

**Upgrading**

The upgrade adds a `Users.LastLoginIp` column; re-running `init.sql` (`docker compose up --build -d`)
applies it. Existing users show no IP until their next login.

## 9. Hosted mode

Set `FILEVAULT_MODE=hosted` for a public instance with limited resources. The default,
`self-hosted`, removes nothing. Any other value stops the API at start.

In hosted mode:

- An account with no sign-in and no use of the app for 30 days is removed along with its files.
  Staying signed in and using the app counts as use. Administrator, permanent and suspended
  accounts are never removed (mark an account permanent on the Admin page).
- 7 days before the removal a warning email is sent, only to an address that was confirmed.
  A user who signs in again before then
  keeps the account. Unsuspending an account, or removing its permanent mark,
  restarts its clock.
- New users see a dismissible notice on the dashboard on first login saying unused accounts are
  removed. Administrators and permanent accounts don't see it. `HOSTED_CONTACT_EMAIL` adds the
  address to ask for a permanent account; leave it empty to leave that sentence out.
- The Admin page gains "Last active" and "Removal due" columns.
- Sign-ups are rate limited (below).
- **SMTP is required.** The API refuses to start in hosted mode without a working email setup:
  `SMTP_HOST` and `SMTP_FROM` (or `SMTP_USER`), plus `SMTP_PORT`, `SMTP_USER` and `SMTP_PASSWORD` as your
  server needs. Without email a hosted site could not confirm sign-ups or reset passwords, and the
  links would be written to the log, where anyone who can read it could use them. The server is not
  contacted at start, so an outage of the mail server does not stop a restart (emails that fail to
  send are logged by subject only).
- A new account must confirm its email before it can sign in (this is the case in both modes), and
  creating a share link also checks that the address is confirmed. Unconfirmed accounts are removed
  by the inactivity job like any other unused account.

The job runs with the hourly storage cleanup. These settings go in `appsettings.json`, or on the
`api` service as environment variables:

| Setting | Default | What it is |
|---|---|---|
| `Hosted:InactiveDays` | 30 | Days without sign-in or use before removal |
| `Hosted:WarningDays` | 7 | Days of warning before removal (kept below `InactiveDays`) |
| `Hosted:MaxRemovalsPerRun` | 50 | Most accounts warned, and most removed, per run |
| `Hosted:MaxRemovalsPerDay` | 200 | Most accounts removed in any 24 hours (a safety brake; counted per API process) |
| `Hosted:DefaultQuotaBytes` | unset | Default quota for accounts that are not permanent or administrators. Unset (or 0) means the same as `Storage:DefaultQuotaBytes` (1 GB). Permanent accounts and administrators always use `Storage:DefaultQuotaBytes`; a quota set on a user always wins |
| `RateLimiting:signup:PermitLimit`, `RateLimiting:signup:WindowSeconds` | 5, 3600 | Sign-ups per IP address per window (5 per hour). Self-hosted installs keep using the login limit (`RateLimiting:auth:*`, 10 per minute) for sign-ups. Must be above 0: `0` does not turn the limit off, the API refuses to start |
| `Hosted:MaxSignupsPerHour`, `Hosted:MaxSignupsPerDay` | 0, 0 | Site-wide limit on new accounts in the last hour / day; 0 = off. Over the limit, sign-up returns 429 "New sign-ups are paused". Accounts an administrator creates are never limited |

**Sign-up limits and your proxy.** The per-IP limit uses the client's address, which behind a
reverse proxy is correct only with `BEHIND_HTTPS_PROXY=true` (section 1). Without it every visitor
shares the proxy's address and the limit becomes a limit for the whole site; the API logs a warning
at start in hosted mode. With it on, keep the API port reachable only through the proxy and set
`ForwardedHeaders__KnownProxies__0` to the proxy's address, otherwise a caller can spoof
`X-Forwarded-For` and dodge the per-IP limit. The site-wide `Hosted:MaxSignupsPerHour/Day` limits
are the backstop; switch them on if sign-up spam appears. Note that they let an attacker pause
sign-ups for up to an hour or a day; you can still create accounts on the Admin page. Nothing about
the visitor's address is stored by the limiter.

**Quota tiers.** Which default an account gets is computed, not stored: changing
`Hosted:DefaultQuotaBytes`, or marking an account permanent (or removing the mark), changes its quota
at once. Lowering it on a live install shrinks existing non-permanent accounts' quotas; users over
the new quota keep their files but can't upload until they are under it. The Admin page shows the
second sentence ("Permanent accounts and admins get ...") only when the two differ.

**Switching an existing install to hosted**

Accounts that are already stale are not removed straight away. Each gets at least the warning
period (7 days by default) from the first run, whether or not an email could be sent. Before
turning it on, mark any account you want to keep as permanent or as an administrator. If you set
`Hosted:DefaultQuotaBytes`, accounts without a quota of their own that are neither permanent nor
administrators drop to it at once (see "Quota tiers").

**Self-hosted without SMTP.** Without `SMTP_HOST` a self-hosted API writes every email to its log,
link included (that is how you confirm accounts locally): a password-reset link is valid for 1 hour
and a confirmation link for 24 hours, and anyone who can read the log can use them. The API logs a
warning at start about that, and Docker keeps only 3 log files of 10 MB for the `api` container. If an
install that ran publicly without SMTP is switched to hosted, the old links expire within 24 hours;
`docker compose up -d --force-recreate api` also discards the container's log straight away.

**Upgrading**

An existing hosted install without SMTP stops at the next start with a message naming `SMTP_HOST` and
`SMTP_FROM` until they are set.

The upgrade adds the `Users.LastActiveAt`, `Users.InactivityWarnedAt` and
`Users.HostedNoticeDismissedAt` columns, even if you stay self-hosted; re-running `init.sql`
(`docker compose up --build -d`) applies them. Existing accounts start their inactivity clock from
their last login, or when they were created.
