# FileVault

**FileVault** is a secure, self-hosted file storage web application. Upload, organise, preview and download your files through a desktop-style interface, with every file encrypted on disk. Designed as a portfolio project to showcase full-stack development skills.

## Features

**Files**
- Upload, download (single files or zips), rename, move, copy and delete files and folders
- Desktop-style file and folder icons, grid and list views, sortable columns
- Details pane with name, type, size, created/modified dates and a preview (images, video, audio, PDF and text)
- A name that's already taken gets a number, like Windows Explorer: `report (1).pdf`
- New accounts start with Documents, Pictures, Music and Videos folders

**Security**
- Files encrypted at rest with AES-256-GCM, each with its own key
- Short-lived access tokens with a rotating, httpOnly refresh-token cookie
- Password rules, rate limiting and per-user storage quotas

**Account and appearance**
- Log in with email; first and last name, profile picture, password change, account deletion
- Light, dark and system themes, and a choice of accent colour

**Tooling**
- Interactive API docs (Swagger) at `/swagger`
- Desktop app (Electron) for Windows, macOS and Linux
- Runs with one command using Docker Compose
- CI on every push: backend build and tests, frontend build and lint, Docker images

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
cp .env.example .env      # fill in MSSQL_SA_PASSWORD, JWT_KEY and ENCRYPTION_MASTER_KEY
docker compose up --build
```

Then open http://localhost:5173 (the API is on http://localhost:3000). Compose starts SQL Server, waits for it to be healthy, applies `Backend/Docker/db/init.sql`, then starts the API and the nginx-served frontend. The database isn't exposed outside the Docker network.

`init.sql` runs on every start and only adds what's missing, so pulling new code and running `docker compose up --build -d` also upgrades an existing database.

Data lives in two named volumes, `filevault_sql_data` and `filevault_file_storage` (the encrypted files). Back both up together with `ENCRYPTION_MASTER_KEY`. `docker compose down` keeps them; `docker compose down -v` deletes them.

To open the database in a GUI tool (SSMS, Azure Data Studio, VS Code SQLTools), copy `docker-compose.override.example.yml` to `docker-compose.override.yml` and restart with `docker compose up -d`. SQL Server is then reachable from this machine only at `127.0.0.1,1434`, user `sa`, with `MSSQL_SA_PASSWORD` from `.env`. Use `127.0.0.1` rather than `localhost`, which some tools resolve to IPv6 and then time out. To look at the stored files, run `docker compose exec api ls -l /data/storage`. They are encrypted, so download them through the app to read them.

The frontend bundle has the API URL compiled in. If the browser reaches the API somewhere other than `http://localhost:3000`, set `API_URL` (and `FRONTEND_URL` for CORS) in `.env` and rebuild.

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
- Set `Encryption:MasterKey` to a base64-encoded 32-byte key (e.g. `openssl rand -base64 32`). **Back this key up.** Every stored file is encrypted with a key that is itself encrypted with this one, so losing it means losing every file.
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
cd Frontend && npm run lint     # frontend lint
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

Interactive API docs are served by the API at **http://localhost:3000/swagger**, and the OpenAPI document is at `/swagger/v1/swagger.json` (it can be imported into Postman or Insomnia).

To try protected endpoints, call `POST /user/login`, then click **Authorize** and paste the returned access token (it lasts 15 minutes). Endpoints are grouped as:

- **Account**: register, login, refresh, logout, profile, password, avatar, storage usage, delete account
- **Files**: upload, folders, list, download (single file or zip), rename, move, copy, delete
- **Health**: `/ping` and `/pingsql`

## Project layout

| Folder | Contents |
|---|---|
| `Backend/` | .NET 8 API: routes, services, models, `Docker/` (dev SQL Server and `init.sql`) |
| `Backend.Test/` | xUnit integration tests |
| `Frontend/` | React app; the file manager UI is in `src/FileManager` ([credits](Frontend/src/FileManager/README.md)) |
| `Desktop/` | Electron desktop app |
| `docs/` | [Roadmap](docs/ROADMAP.md) and the original [codebase review](docs/CODEBASE_REVIEW.md) |

## Roadmap

Planned features are listed in [docs/ROADMAP.md](docs/ROADMAP.md).

## License

This project is licensed under the [MIT License](LICENSE). The file manager UI is adapted from
[@cubone/react-file-manager](https://github.com/Saifullah-dev/react-file-manager), also MIT licensed.

## Author

Christian Jones
[GitHub](https://github.com/ChrisLPJones)
