# FileVault

**FileVault** is a secure, self-hosted file storage web application. Upload, organise, preview and download your files through a desktop-style interface, with every file encrypted on disk. Designed as a portfolio project to showcase full-stack development skills.

## Features

**Files**
- Upload, download (single files or zips), rename, move, copy and delete files and folders
- Big files upload in 8 MB chunks that resume after a dropped connection (up to 2 GB each by default); whole folders can be uploaded too
- Desktop-style file and folder icons, image thumbnails, grid and list views, sortable columns
- Details pane with name, type, size, created/modified dates and a preview (images, video, audio, PDF and text)
- Search every file and folder by name from the top bar
- Favourites and recently opened files at the top of the folder tree
- Recycle bin: deleted items can be restored, and are removed for good after 30 days
- A name that's already taken gets a number, like Windows Explorer: `report (1).pdf`
- New accounts start with Documents, Pictures, Music and Videos folders
- Works on phones and tablets: the folder tree becomes a drawer and the details pane a bottom sheet

**Sharing**
- Share links to a file or folder, with an optional expiry date and password; folders download as a zip
- A Shared links page to copy links, show their passwords again and revoke them

**Security**
- Files encrypted at rest with AES-256-GCM, each with its own key; share link secrets and authenticator keys are encrypted too
- Two-factor authentication with an authenticator app, plus single-use recovery codes
- See every device you're logged in on, and sign any of them out
- Email confirmation before the first login, and password reset by email
- Short-lived access tokens with a rotating, httpOnly refresh-token cookie
- Password rules, rate limiting and per-user storage quotas, and a disk limit: uploads are refused once the storage drive is more than 80 % full (`STORAGE_MAX_DISK_USE_PERCENT`, `0` turns it off)
- Strict Content-Security-Policy and security headers; ready to run behind an HTTPS reverse proxy

**Account and appearance**
- Log in with email; first and last name, profile picture, password change, account deletion
- Changing your email address needs your password and is confirmed by a link sent to the new address (open it while signed in); until then the current address stays your login, and the old address (if confirmed) gets a "wasn't me" link that cancels the change and signs out every device
- Light, dark and system themes, and a choice of accent colour
- An admin page for the server owner: every account's avatar, storage use and quota, each user's last login IP address and country (looked up in a local database, nothing sent to a third party), and server totals; create accounts (active immediately, email confirmed), set a user's password (signs them out everywhere), mark non-admin accounts permanent (admins are always permanent), suspend and unsuspend accounts (a suspended user is signed out at once and can't sign in; files and shares are kept, share links show "not found" until unsuspended), and delete accounts (type the email to confirm; the last admin can't be deleted)
- Optional hosted mode (`FILEVAULT_MODE=hosted`) for a public instance: accounts unused for 30 days are removed with their files (after a warning email; hosted mode requires SMTP), and new users see a dismissible notice. Administrator, permanent and suspended accounts are never removed. Hosted mode also rate limits sign-ups (5 per IP per hour by default, optional site-wide hourly and daily limits), can give ordinary accounts a smaller default quota, and checks for a confirmed email before a share link is created. Self-hosted is the default and removes nothing

**Tooling**
- Interactive API docs (Swagger) at `/swagger` in development, or with `SWAGGER_ENABLED=true`
- Desktop app (Electron) for Windows, macOS and Linux
- Runs with one command using Docker Compose
- CI on every push: backend build and integration tests, frontend lint and tests (Vitest), Docker images

## Screenshots

The file manager, with the folder tree, Favourites and Recent on the left and thumbnails for images:

![File manager in grid view, light theme](docs/screenshots/files-grid-light.png)

The same view in the dark theme:

![File manager in grid view, dark theme](docs/screenshots/files-grid-dark.png)

Selecting a file shows its details and a preview on the right:

![Details pane with an image preview](docs/screenshots/details-pane.png)

List view, with sortable columns and a star to add items to Favourites:

![File manager in list view](docs/screenshots/files-list.png)

Searching every folder from the top bar, and the recycle bin:

![Search results](docs/screenshots/search.png)

![Recycle bin](docs/screenshots/recycle-bin.png)

Share links: the Shared links page, where a link's password can be shown again, and what someone opening a folder link sees:

![Shared links page](docs/screenshots/shared-links.png)

![Public page for a shared folder](docs/screenshots/share-page.png)

Logging in, and the Settings page (storage, theme and accent colour, profile; two-factor authentication and the devices you're logged in on):

![Login page](docs/screenshots/login.png)

![Settings page](docs/screenshots/settings.png)

![Two-factor authentication and active sessions in Settings](docs/screenshots/settings-security.png)

On a phone, the folder tree opens as a drawer and the less-used toolbar actions move into a menu:

<p>
  <img src="docs/screenshots/phone-grid.png" alt="File manager on a phone" width="300">
  <img src="docs/screenshots/phone-drawer.png" alt="Folder drawer on a phone" width="300">
</p>

## Technologies Used

- **Backend**: C# (.NET 8 Minimal APIs), ADO.NET
- **Database**: SQL Server
- **Frontend**: React 18 (Vite)
- **Desktop**: Electron
- **Storage**: Filesystem-based storage, encrypted
- **Containerization**: Docker + Docker Compose

## Run everything with Docker

Only [Docker](https://www.docker.com/) is needed for this option.

```bash
git clone https://github.com/ChrisLPJones/FileVault.git
cd FileVault
cp .env.example .env

# Fill in the four secrets with random values
sed -i.bak \
  -e "s|^MSSQL_SA_PASSWORD=.*|MSSQL_SA_PASSWORD=$(openssl rand -base64 24 | tr -d '/+=')Aa1|" \
  -e "s|^MSSQL_APP_PASSWORD=.*|MSSQL_APP_PASSWORD=$(openssl rand -base64 24 | tr -d '/+=')Aa1|" \
  -e "s|^JWT_KEY=.*|JWT_KEY=$(openssl rand -base64 48)|" \
  -e "s|^ENCRYPTION_MASTER_KEY=.*|ENCRYPTION_MASTER_KEY=$(openssl rand -base64 32)|" \
  .env && rm .env.bak

docker compose up --build -d
```

Back up `.env` somewhere safe, above all `ENCRYPTION_MASTER_KEY`: without it the stored files can't be decrypted.

Then open http://localhost:5173 (the API is on http://localhost:3000). Compose starts SQL Server, waits for it to be healthy, applies `Backend/Docker/db/init.sql`, then creates a least-privilege database login for the API (`filevault_app`, password `MSSQL_APP_PASSWORD`: it can read and write the data but not change the schema, and the API container never gets the `sa` password), then starts the API and the nginx-served frontend. The database isn't exposed outside the Docker network. Updating an existing install? Add `MSSQL_APP_PASSWORD` to `.env` first; see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md#2-secrets-and-settings).

`init.sql` runs on every start and only adds what's missing, so pulling new code and running `docker compose up --build -d` also upgrades an existing database.

Data lives in two named volumes, `filevault_sql_data` and `filevault_file_storage` (the encrypted files). Back both up together with `ENCRYPTION_MASTER_KEY`. `docker compose down` keeps them; `docker compose down -v` deletes them.

To open the database in a GUI tool (SSMS, Azure Data Studio, VS Code SQLTools), copy `docker-compose.override.example.yml` to `docker-compose.override.yml` and restart with `docker compose up -d`. SQL Server is then reachable from this machine only at `127.0.0.1,1434`, user `sa`, with `MSSQL_SA_PASSWORD` from `.env`. Use `127.0.0.1` rather than `localhost`, which some tools resolve to IPv6 and then time out. To look at the stored files, run `docker compose exec api ls -l /data/storage`. They are encrypted, so download them through the app to read them.

New accounts confirm their email address before they can log in, and "Forgot password?" sends a reset link. To send those emails, set the `SMTP_*` settings in `.env`; they are required in hosted mode, where the API won't start without them. On a self-hosted setup without them the API writes each email, link included, to its log instead (with a warning at start; anyone who can read the log can use the links), so on a local setup you can find the link with `docker compose logs api`.

The first account registered on a new install becomes the administrator and gets an Admin link in the account menu (`/admin`: every account's storage use and quota, server totals, and who else is an administrator). On a public install, register straight after the first start, or set `INITIAL_ADMIN_EMAIL` in `.env` to your address so only that account can become the administrator, once its email is confirmed (see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)). Admin rights are chosen only when an administrator creates an account (Administrator tick box); there is always at least one administrator.

If the browser reaches the API somewhere other than `http://localhost:3000`, set `API_URL` (and `FRONTEND_URL` for CORS) in `.env`; the frontend container reads `API_URL` when it starts, so `docker compose up -d` is enough. After pulling schema changes use `docker compose up --build -d`.

The Admin page can show the country of each user's last login. That needs a free MaxMind GeoLite2 Country database, which Compose can download for you with `docker compose --profile geoip up -d` once `GEOIPUPDATE_ACCOUNT_ID` and `GEOIPUPDATE_LICENSE_KEY` are set in `.env`. Without it the country shows "Unknown". See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md#8-last-login-location-geoip).

To run a public instance that cleans up unused accounts, set `FILEVAULT_MODE=hosted` in `.env`, and optionally `HOSTED_CONTACT_EMAIL` (the address the notice tells users to email to ask for a permanent account). Set `ABUSE_CONTACT_EMAIL` to show a "Report abuse" address on the sign-in, sign-up, share and settings pages (hosted mode falls back to `HOSTED_CONTACT_EMAIL`). Anything other than `self-hosted` or `hosted` stops the API starting. See "Hosted mode" in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

Hosting it for real (HTTPS reverse proxy, secrets, backups, key rotation, security headers): see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Local development

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/)
- [Docker](https://www.docker.com/) (for SQL Server)
- [Node.js](https://nodejs.org/) 20 or later

### Configure secrets

Secrets are not committed. Create these gitignored files from the templates:

```bash
# SQL Server SA password used by Docker
cp Backend/Docker/.env.example Backend/Docker/.env

# API connection string, JWT signing key and file encryption key
cp Backend/appsettings.Development.example.json Backend/appsettings.Development.json

# Where the frontend finds the API
cp Frontend/.env.example Frontend/.env
```

Then edit them:

- Set `MSSQL_SA_PASSWORD` in `Backend/Docker/.env` to a strong password.
- In `Backend/appsettings.Development.json`, use that same password in the connection string.
- Set `Jwt:Key` to a random value of at least 32 bytes (e.g. `openssl rand -base64 48`).
- Set `Encryption:MasterKey` to a base64-encoded 32-byte key (e.g. `openssl rand -base64 32`). **Back this key up.** Every stored file is encrypted with a key that is itself encrypted with this one, so losing it means losing every file. To replace it later without losing anything, see "Rotating the master key" in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md#rotating-the-master-key).
- Point `VITE_API_BASE_URL` in `Frontend/.env` at the API (the port `dotnet run` prints).

Outside Development, supply the same settings as environment variables (`ConnectionStrings__DefaultConnection`, `Jwt__Key`, `Encryption__MasterKey`). The API refuses to start if any of them is missing.

### Start it (three terminals)

```bash
# 1. SQL Server (Docker Desktop must be running). Also applies init.sql.
cd Backend/Docker
docker compose up -d

# 2. API
cd Backend
dotnet run

# 3. Frontend
cd Frontend
npm install
npm run dev
```

### Tests

```bash
dotnet test                     # backend integration tests (needs the SQL Server from step 1)
cd Frontend
npm run lint                    # frontend lint
npm test                        # frontend tests (Vitest and Testing Library)
```

The integration tests create their own throwaway accounts and delete them afterwards. If tests fail with server errors after pulling new code, re-run `docker compose up -d` in `Backend/Docker` to apply any new database changes.

### How files are stored

Uploaded files are encrypted with AES-256-GCM before they reach disk (`Backend/SecureVaultStorage`, named by GUID). Each file has its own random data key, stored in the database encrypted with the master key. Files are encrypted in 64 KB chunks, so downloads stream without loading the whole file into memory and range requests (e.g. video seeking) still work. A file that has been modified on disk fails to decrypt instead of being served.

## Desktop app

`Desktop/` contains an Electron app that opens your FileVault server in its own window, with a
first-run "connect to server" screen, a remembered window position and an installer build:

```bash
cd Desktop
npm install
npm start          # run it
npm run dist       # build the Windows installer (dist/FileVault Setup <version>.exe)
```

See [Desktop/README.md](Desktop/README.md) for details.

## API Documentation

Interactive API docs are served at `/swagger` on the API, and the OpenAPI document is at `/swagger/v1/swagger.json` (it can be imported into Postman or Insomnia). They're on when the API runs in Development (`dotnet run`). The Docker stack runs in Production, where they're off unless you add `SWAGGER_ENABLED=true` to `.env` (then **http://localhost:3000/swagger**); see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md#5-api-docs-swagger).

To try protected endpoints, call `POST /user/login`, then click **Authorize** and paste the returned access token (it lasts 15 minutes). Endpoints are grouped as:

- **Account**: register, login, refresh, logout, profile, password, avatar, email confirmation, password reset, storage usage, delete account
- **Two-factor authentication**: set up, turn on and off, log in with a code, new recovery codes
- **Sessions**: list the devices you're logged in on, sign one or all others out
- **Files**: upload, folders, list, download (single file or zip), rename, move, copy, delete, thumbnails, favourites, recently opened
- **Uploads**: chunked, resumable uploads for big files
- **Recycle bin**: list, restore, delete for good, empty
- **Shares**: create, list and revoke share links, and the public endpoints a link uses
- **Admin**: every account's usage and quota, change quotas, create accounts (optionally as an administrator, with an optional quota), delete accounts, set a user's password, mark non-admin accounts permanent (admins are always permanent), suspend or unsuspend accounts (`PUT /admin/users/{id}/suspended`), server totals
- **Site**: `GET /site`, the public abuse contact address (if one is set)
- **Health**: `/ping` and `/pingsql`

## Project layout

| Folder | Contents |
|---|---|
| `Backend/` | .NET 8 API: routes, services, models, `Docker/` (dev SQL Server and `init.sql`) |
| `Backend.Test/` | xUnit integration tests |
| `Frontend/` | React app and its Vitest tests; the file manager UI is in `src/FileManager` ([credits](Frontend/src/FileManager/README.md)) |
| `Desktop/` | Electron desktop app |
| `docs/` | [Deployment guide](docs/DEPLOYMENT.md) and screenshots |

## License

This project is licensed under the [MIT License](LICENSE). The file manager UI is adapted from
[@cubone/react-file-manager](https://github.com/Saifullah-dev/react-file-manager), also MIT licensed.

This product includes GeoLite2 data created by MaxMind, available from https://www.maxmind.com

## Author

Christian Jones
[GitHub](https://github.com/ChrisLPJones)
