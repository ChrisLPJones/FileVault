# FileVault: project guide

Everything a new engineer needs to work on FileVault. For a user-facing overview and screenshots
see [README.md](README.md); for hosting it for real see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## What it is

A self-hosted file storage web app, built as a portfolio project. Users sign up, then upload,
organise, preview, search, share and download files in a desktop-style file manager. Every file
is encrypted on disk. It runs in a browser, as an Electron desktop app, or both, against one API.

Main features: folders, grid/list views, thumbnails, details pane with previews, search,
favourites and recent files, recycle bin (30 days), chunked resumable uploads (2 GB per file by
default), share links with expiry and password, two-factor authentication, active sessions,
email confirmation and password reset, per-user quotas, an admin page, light/dark themes, and a
phone layout.

## Tech stack

| Layer | What |
|---|---|
| API | C# / .NET 8 minimal APIs, raw ADO.NET (`Microsoft.Data.SqlClient`), no ORM |
| Database | SQL Server 2022 (in Docker) |
| Auth | JWT access tokens (15 min) + rotating refresh token in an httpOnly cookie; BCrypt; TOTP (RFC 6238, own implementation) |
| Crypto | AES-256-GCM, HKDF-SHA256, HMAC-SHA256 (all from `System.Security.Cryptography`) |
| Images | SkiaSharp (thumbnails, avatars) |
| Frontend | React 18 + Vite, axios, react-router; file manager forked from `@cubone/react-file-manager` |
| Frontend tests | Vitest + Testing Library (jsdom) |
| Backend tests | xUnit + FluentAssertions + `WebApplicationFactory`, against a real SQL Server |
| Desktop | Electron (wraps the web app; first-run "connect to server" screen) |
| Serving | nginx (frontend container) with a templated Content-Security-Policy |
| CI | GitHub Actions: backend build + tests (SQL Server service), frontend build + lint + tests, Docker image builds |

## Repository layout

```
Backend/                 .NET 8 API
  Program.cs             Service registration, auth, CORS, rate limits, headers, route mapping
  Routes/                One file per feature: *Routes.cs map endpoints (Swagger tags/summaries live here)
  Services/              Business logic. DatabaseServices is a partial class split per feature:
                         DatabaseService.cs (users/files) + DatabaseService.<Feature>.cs
  Models/                Request/response records and DTOs
  Docker/                Dev SQL Server compose + db/init.sql (the whole schema)
  Dockerfile             API image
Backend.Test/            xUnit integration tests (TestSupport.cs / TestAccounts.cs hold shared helpers)
Frontend/
  src/pages/             Routed pages: Login (+2FA step), Register, Dashboard, Settings, SharedLinks,
                         Share (public link page), Admin, ForgotPassword/ResetPassword/VerifyEmail, Layout
  src/FileManager/       The file manager UI (toolbar, folder tree, list, details pane, bin, search)
  src/contexts/          File manager state: files, navigation, selection, clipboard, layout, search, bin...
  src/api/               API calls; everything goes through the axios client in api.js
  src/hooks/             useUserProfile (one shared /user/info load)
  src/utils/             Theme/accent, formatting, password rules
  nginx/                 default.conf.template + security headers (used by the Docker image)
Desktop/                 Electron app
docs/                    DEPLOYMENT.md, screenshots, original CODEBASE_REVIEW.md
docker-compose.yml       Full stack: SQL Server, schema init, API, frontend
```

## How it fits together

- **Request flow:** browser → frontend (nginx on :5173 in Docker, Vite in dev) → API on :3000 →
  SQL Server. The frontend calls the API directly (CORS), not through nginx. `VITE_API_BASE_URL`
  is compiled into the bundle.
- **Auth:** `POST /user/login` returns an access token (kept in memory by the frontend) and sets
  the `fv_refresh` cookie (httpOnly, SameSite=Strict, path `/user`). The axios client refreshes
  before expiry and on 401. Each login is a *session* (Sessions table); refresh tokens rotate
  within it, and reusing an old one after a 30 s grace window revokes every session.
  With 2FA on, login returns `{ twoFactorRequired, challengeToken }` and `POST /user/login/2fa`
  finishes it.
- **Storage:** files live under `StorageRoot` named by GUID. Each file has a random data key,
  stored in `Files.WrappedKey` encrypted with the master key. Files are encrypted in 64 KB
  AES-GCM chunks, so downloads stream and range requests work.
- **Other secrets:** `SecretProtector` derives one key per use from the master key with HKDF
  (authenticator secrets, share link tokens and passwords, recovery-code hashes). The HKDF info
  strings are part of the stored format; changing one makes existing data unreadable.
- **Schema:** `Backend/Docker/db/init.sql` is the only migration mechanism. It runs on every
  `docker compose up` and is idempotent and additive.
- **Background work:** `StorageCleanupService` empties expired bin entries and abandoned
  chunked uploads; emails are queued and sent in the background.

## Build, run and test

### Run everything in Docker (only Docker needed)

```bash
cp .env.example .env    # set MSSQL_SA_PASSWORD, JWT_KEY (openssl rand -base64 48),
                        # ENCRYPTION_MASTER_KEY (openssl rand -base64 32)
docker compose up --build -d
```

Open http://localhost:5173 (API on http://localhost:3000). Optional `.env` settings:
`ADMIN_EMAILS` (comma-separated), `SMTP_*`, `SWAGGER_ENABLED=true`, `BEHIND_HTTPS_PROXY=true`,
`API_URL`/`FRONTEND_URL`. Data is in the `filevault_sql_data` and `filevault_file_storage`
volumes; `docker compose down -v` deletes them.

### Local development (three terminals)

One-time setup (all three files are gitignored):

```bash
cp Backend/Docker/.env.example Backend/Docker/.env                                    # MSSQL_SA_PASSWORD
cp Backend/appsettings.Development.example.json Backend/appsettings.Development.json  # connection string, Jwt:Key, Encryption:MasterKey
cp Frontend/.env.example Frontend/.env                                                # VITE_API_BASE_URL=http://localhost:3000
```

```bash
cd Backend/Docker && docker compose up -d      # SQL Server on :1433, applies init.sql
cd Backend && dotnet run                       # API on http://localhost:3000, Swagger at /swagger
cd Frontend && npm install && npm run dev      # http://localhost:5173
```

### Tests

```bash
dotnet test                     # repo root; needs the dev SQL Server above
cd Frontend
npm test                        # Vitest
npm run lint                    # ESLint (CI fails on errors; ~36 old warnings are known)
npm run build
```

After pulling changes that touch `init.sql`, run `docker compose up -d` in `Backend/Docker`
again before testing, or tests fail with SQL errors.

### Desktop app

```bash
cd Desktop && npm install
npm start        # run against a FileVault server
npm run dist     # Windows installer in Desktop/dist/
```

## Conventions

**Git and PRs**
- Never commit to `master`. One branch per piece of work, cut from an up-to-date `master`, with a
  descriptive name (`share-links`, `file-times-utc`). Open a PR; merge only when CI is green.
- Delete the branch (remote and local) once its PR is merged.
- Commits are authored as `ChrisLPJones <killfredd@gmail.com>`, with no co-author trailers.
  Messages: a short imperative subject, then a body explaining what changed and why (bullets fine).
- Never commit secrets. Gitignored: root `.env`, `Backend/Docker/.env`,
  `Backend/appsettings.Development.json`, `Frontend/.env`, `Backend/Docker/.vscode/settings.json`,
  and `Backend/SecureVaultStorage/`.

**Backend**
- New endpoints go in `Routes/<Feature>Routes.cs` with `.WithTags()`, `.WithSummary()` and
  `.Produces<>()` so Swagger stays complete; protect them with `.RequireAuthorization()`, and add a
  rate-limit policy for anything public or credential-related.
- New queries go in `Services/DatabaseService.<Feature>.cs` (the partial class), parameterised
  `SqlCommand`s only.
- Schema changes: append an idempotent block to `init.sql` (`IF NOT EXISTS` /
  `IF COL_LENGTH(...) IS NULL`) and update the `CREATE TABLE` for fresh installs too. Never edit
  an existing block in a way that breaks databases that already ran it.
- Store all times in UTC (`SYSUTCDATETIME()` / `DateTime.UtcNow`) and read them with
  `DateTime.SpecifyKind(..., DateTimeKind.Utc)` so the JSON carries `Z`.
- Anything that writes or reads across many users' `Files` rows goes through
  `RetryOnDeadlockAsync` (see Gotchas).
- Every possible failure on public share endpoints returns the same 404, so visitors learn
  nothing about which links exist.
- The API must refuse to start if a required secret is missing (connection string, `Jwt:Key`,
  `Encryption:MasterKey`).

**Tests**
- Integration tests use real accounts against the dev database and delete them afterwards.
  New accounts must confirm their email before logging in: use `TestAccounts.CreateAsync(factory, ...)`
  or `TestDatabase.MarkEmailVerifiedAsync`.
- A bug fix comes with a test that fails without the fix.

**Frontend**
- Colours come from CSS variables (`--fv-*` in `src/styles/theme.css`) so light/dark/accent work;
  no hard-coded colours.
- All API calls go through `src/api/api.js` (token handling and refresh live there).
- Every page must work at phone width (390 px).
- Changes to the vendored file manager are listed in `Frontend/src/FileManager/README.md`.

## Decisions and why

| Decision | Why |
|---|---|
| Envelope encryption: per-file key wrapped by one master key | A leaked database alone reveals nothing; rotating the master key only needs keys re-wrapped, not files re-encrypted |
| 64 KB AES-GCM chunks | Streaming downloads and video seeking without decrypting whole files; tampering is detected per chunk |
| Access token in memory + httpOnly refresh cookie | No token reachable by page scripts beyond 15 minutes; refresh cookie is invisible to JavaScript |
| `init.sql` instead of a migration framework | One idempotent script that both creates and upgrades; runs automatically on `docker compose up` |
| Email must be confirmed before first login | Product decision |
| All file types allowed, downloads always `attachment` + `nosniff` | Product decision ("industry standard"); serving as attachments stops uploaded HTML/SVG running in the app's origin |
| 2 GB per-file limit (`Storage:MaxFileBytes`) | 10 GB was judged too big; configurable |
| Share link token stored as SHA-256 hash **and** encrypted copy | Hash for public lookup without decrypting; encrypted copy so owners can copy the link and reveal the password later (requested) |
| Turning on 2FA signs out all other sessions | Product decision: sessions opened before 2FA never gave a code |
| Admins only via `ADMIN_EMAILS`; no granting in the app; removing an email revokes admin | Only the server owner controls admin; checked against the DB on every admin request |
| "Recent" = files opened (double-click/Enter/Open) or downloaded, not selected | Product decision, matches Windows Explorer |
| Recycle bin is a soft delete; binned files still count towards quota | Restore must be exact; space is only freed on permanent delete |
| Thumbnails made lazily on first request, encrypted, not counted in quota | Works for files uploaded before thumbnails existed |
| Font bundled (`@fontsource-variable/nunito-sans`), not Google Fonts | Strict CSP and no visitor IPs sent to Google |
| Swagger off in production unless `SWAGGER_ENABLED=true` | Don't advertise the API surface on public deployments |
| Master key rotation tool deferred | Not needed until a key may have leaked; on the roadmap |

## Gotchas

- **Shared dev database.** Tests and the dev API use the same SQL Server. Re-apply `init.sql`
  (`docker compose up -d` in `Backend/Docker`) after switching to a branch with schema changes.
- **`init.sql` merges.** When two branches both append to `init.sql`, check every block still ends
  with `END` / `GO` after resolving conflicts. A fresh install is the only real test: with your
  own stack stopped (it uses the same ports), run `docker compose -p fvtest up --build -d`
  (a separate project, so separate, empty volumes), check `docker logs fvtest-db-init-1` has no
  `Msg` errors and the container exited 0, then `docker compose -p fvtest down -v`.
- **Deadlocks under parallel tests.** The recursive delete trigger walks `Files`; concurrent
  deletes or cross-user reads (admin page) can be picked as deadlock victims (SQL error 1205).
  The trigger forces an index seek and the affected calls retry; new code touching many users'
  rows needs the same retry.
- **Login rate limit:** 10 logins per minute per IP address. Scripts that log in repeatedly get 429.
- **No SMTP configured** → confirmation and reset emails (with links) are written to the API log
  (`docker compose logs api`). This is how you confirm accounts locally.
- **CSP and the API origin.** The nginx CSP is generated from `API_URL`. The public share page
  downloads by posting a form to the API into a hidden frame, so `form-action` and `frame-src`
  must include the API origin. Changing `API_URL` needs a frontend rebuild.
- **Times:** anything not marked UTC is shown shifted by the viewer's offset. Rows written before
  the UTC fix on a SQL Server outside Docker (non-UTC clock) keep local times.
- **The master key.** Losing `ENCRYPTION_MASTER_KEY` loses every file, share secret and 2FA setup.
  Back it up with both Docker volumes.
- **Lint warnings:** about 36 existing warnings, mostly in the vendored file manager. CI only fails
  on errors; don't add new warnings.
- **Headless Chrome** shows PDF previews blank; that's the browser, not the app.
- **Line endings:** the repo has some CRLF files; `.gitattributes` keeps shell scripts LF (the
  nginx container runs them).

## Known limitations

- Search is client-side over the already-loaded file list.
- Thumbnails only for JPEG, PNG, GIF, WebP and BMP (no video/PDF).
- Share links created before tokens were encrypted can be revoked but not copied or have their
  password shown.
- Non-browser clients appear as "Unknown device" in the sessions list. Turning on 2FA from a
  client without the refresh cookie signs out every session, including its own.
- No master key rotation tool yet.

## What's next

Roughly in priority order:

1. Master key rotation command (re-wrap file keys and stored secrets under a new key)
2. Sharing with other FileVault accounts (read-only or edit)
3. File versions
4. Video/PDF thumbnails, rendered Markdown previews
5. Activity log in Settings
6. Admin tools: disable/delete accounts, storage over time
7. Releases: versioned images on GHCR and desktop installers from CI on a tag
