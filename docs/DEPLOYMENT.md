# Deploying FileVault

How to run FileVault for real: behind HTTPS, with the right secrets, backed up, and with the
security settings that matter. The quick start in the [README](../README.md#run-everything-with-docker)
gets it running on `localhost`; this guide picks up from there.

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
API_URL=https://api.files.example.com         # compiled into the frontend, and used in its CSP
BEHIND_HTTPS_PROXY=true                       # see below
```

`API_URL` is a build argument, so rebuild after changing it: `docker compose up --build -d`.

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
without the first three.

| Variable | What it is |
|---|---|
| `MSSQL_SA_PASSWORD` | SQL Server `sa` password. Only applied when the database volume is first created |
| `JWT_KEY` | Signs access tokens. At least 32 bytes: `openssl rand -base64 48` |
| `ENCRYPTION_MASTER_KEY` | Encrypts every file's key, and the two-factor secrets. Exactly 32 bytes, base64: `openssl rand -base64 32` |
| `FRONTEND_URL` | Where the browser loads the app; the only origin the API accepts calls from |
| `API_URL` | Where the browser reaches the API. Build-time: rebuild after changing it |
| `BEHIND_HTTPS_PROXY` | `true` behind an HTTPS reverse proxy (section 1) |
| `SWAGGER_ENABLED` | `true` to serve the API docs (section 5). Off by default |
| `FILEVAULT_MODE` | Optional: `self-hosted` (default) or `hosted` (section 9). Anything else stops the API starting |
| `HOSTED_CONTACT_EMAIL` | Optional, hosted mode only: the address users are told to email to ask for a permanent account |
| `INITIAL_ADMIN_EMAIL` | Optional, recommended on a public install: the one email that can become the first administrator (section 7) |
| `GEOIPUPDATE_ACCOUNT_ID`, `GEOIPUPDATE_LICENSE_KEY` | Optional: MaxMind credentials for the `geoip` profile, which downloads the country database (section 8) |

Other API settings can be passed as environment variables on the `api` service using
`Section__Key` names, for example `Storage__DefaultQuotaBytes`, `Storage__MaxUploadBytes`,
`RateLimiting__auth__PermitLimit` or `TwoFactor__Issuer` (the name shown in authenticator apps).
See `Backend/appsettings.json` for the full list and defaults.

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
and every two-factor secret, is unreadable.

A simple consistent backup stops the API and the database briefly (SQL Server's data files can't
be copied safely while it's running):

```bash
docker compose stop api sqlserver
docker run --rm -v filevault_sql_data:/data -v "$PWD/backup:/backup" alpine \
    tar czf /backup/sql_data-$(date +%F).tar.gz -C /data .
docker run --rm -v filevault_file_storage:/data -v "$PWD/backup:/backup" alpine \
    tar czf /backup/file_storage-$(date +%F).tar.gz -C /data .
docker compose start sqlserver api
```

Restore by extracting both archives into empty volumes of the same names before starting the stack,
with the same `ENCRYPTION_MASTER_KEY` in `.env`.

## 4. Rotating the JWT key

Change `JWT_KEY` in `.env` and run `docker compose up -d`. Every access token signed with the old key
stops working at once. Refresh tokens are random values checked against the database, not signed,
so an open browser tab quietly gets a new access token on its next request and stays logged in.

To log **everyone** out as well (for example after a suspected leak), also revoke every refresh token:

```bash
docker compose exec sqlserver sh -c '/opt/mssql-tools18/bin/sqlcmd -C -U sa -P "$MSSQL_SA_PASSWORD" \
    -d SecureVaultDb -Q "UPDATE RefreshTokens SET RevokedAt = SYSUTCDATETIME() WHERE RevokedAt IS NULL"'
```

Users then log in again (with their two-factor code if they use one). Their files are unaffected.

Don't rotate `ENCRYPTION_MASTER_KEY` this way: changing it makes every stored file unreadable.
There is no master-key rotation tool yet.

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
container works out the API origin from `API_URL` and writes the real config. To check the headers:

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
start or by a password reset. Emails are compared exactly (look-alike characters don't match).

Email delivery needs the `SMTP_*` settings (or find the links in `docker compose logs api`).

**If someone registered your address first** (a new sign-up), use "Forgot password?" for it. The
reset gives you the account and the squatter's password stops working. If someone instead changed
their existing account's email to your address, use a different address for `INITIAL_ADMIN_EMAIL`,
or have an operator remove that account.

Use a plain-ASCII, lower-case address for `INITIAL_ADMIN_EMAIL`. Someone registering a look-alike
address first (for example with `ß` instead of `ss`) can't become administrator, but can stop you
registering that address. Fix it by choosing a different `INITIAL_ADMIN_EMAIL` or removing that
account.

Once there is an administrator, they can grant or remove admin rights for other accounts on the
Admin page ("Admin rights" column). There must always be at least one administrator: removing the
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
- Changing admin rights takes a short database lock. If it can't be had in time the API answers
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
- 7 days before the removal a warning email is sent, only to an address that was confirmed and only
  if SMTP is set up (the `SMTP_*` settings). Without SMTP no warning is sent, but the removal still
  happens (the API logs a warning at startup in that case). A user who signs in again before then
  keeps the account. Unsuspending an account, or removing its admin rights or permanent mark,
  restarts its clock.
- New users see a dismissible notice on the dashboard on first login saying unused accounts are
  removed. Administrators and permanent accounts don't see it. `HOSTED_CONTACT_EMAIL` adds the
  address to ask for a permanent account; leave it empty to leave that sentence out.
- The Admin page gains "Last active" and "Removal due" columns.

The job runs with the hourly storage cleanup. These settings go in `appsettings.json`, or on the
`api` service as environment variables:

| Setting | Default | What it is |
|---|---|---|
| `Hosted:InactiveDays` | 30 | Days without sign-in or use before removal |
| `Hosted:WarningDays` | 7 | Days of warning before removal (kept below `InactiveDays`) |
| `Hosted:MaxRemovalsPerRun` | 50 | Most accounts warned, and most removed, per run |
| `Hosted:MaxRemovalsPerDay` | 200 | Most accounts removed in any 24 hours (a safety brake; counted per API process) |

**Switching an existing install to hosted**

Accounts that are already stale are not removed straight away. Each gets at least the warning
period (7 days by default) from the first run, whether or not an email could be sent. Before
turning it on, mark any account you want to keep as permanent or as an administrator.

**Upgrading**

The upgrade adds the `Users.LastActiveAt`, `Users.InactivityWarnedAt` and
`Users.HostedNoticeDismissedAt` columns, even if you stay self-hosted; re-running `init.sql`
(`docker compose up --build -d`) applies them. Existing accounts start their inactivity clock from
their last login, or when they were created.
