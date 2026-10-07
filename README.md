# FileVault

**FileVault** is a secure file-sharing web application. It allows users to upload, download, manage, and delete files securely through a user-friendly interface. Designed as a portfolio project to showcase full-stack development skills.

## Features

- Secure user authentication (register & login)
- Upload and download files
- File metadata (filename, upload date, size, type)
- Delete files
- SQL Server backend with metadata tracking
- Encrypted Files stored securely on disk
- Dockerized for easy deployment

## Technologies Used

- **Backend**: C# (.NET 8 Minimal APIs)
- **Database**: SQL Server
- **Frontend**: ReactJS
- **Storage**: Filesystem-based storage
- **Containerization**: Docker + Docker Compose

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/)
- [Docker](https://www.docker.com/)
- Node.js & npm
 (for React frontend)
- React 18+
 (installed via create-react-app or Vite)

### Clone the Repository

```bash
git clone https://github.com/ChrisLPJones/FileVault.git
cd FileVault
```

## Run everything with Docker

Only Docker is needed for this option.

```bash
cp .env.example .env      # fill in MSSQL_SA_PASSWORD, JWT_KEY and ENCRYPTION_MASTER_KEY
docker compose up --build
```

Then open http://localhost:5173 (the API is on http://localhost:3000). Compose starts SQL Server, waits for it to be healthy, applies `Backend/Docker/db/init.sql`, then starts the API and the nginx-served frontend. The database isn't exposed outside the Docker network.

Data lives in two named volumes, `filevault_sql_data` and `filevault_file_storage` (the encrypted files). Back both up together with `ENCRYPTION_MASTER_KEY`. `docker compose down` keeps them; `docker compose down -v` deletes them.

To open the database in a GUI tool (SSMS, Azure Data Studio, VS Code SQLTools), copy `docker-compose.override.example.yml` to `docker-compose.override.yml` and restart with `docker compose up -d`. SQL Server is then reachable from this machine only at `127.0.0.1,1434`, user `sa`, with `MSSQL_SA_PASSWORD` from `.env`. Use `127.0.0.1` rather than `localhost`, which some tools resolve to IPv6 and then time out. To look at the stored files, run `docker compose exec api ls -l /data/storage`. They are encrypted, so download them through the app to read them.

The frontend bundle has the API URL compiled in. If the browser reaches the API somewhere other than `http://localhost:3000`, set `API_URL` (and `FRONTEND_URL` for CORS) in `.env` and rebuild.

## Local development

### Configure secrets

Secrets are not committed. Create these two gitignored files from the templates:

```bash
# SQL Server SA password used by Docker
cp Backend/Docker/.env.example Backend/Docker/.env

# API connection string, JWT signing key and file encryption key
cp Backend/appsettings.Development.example.json Backend/appsettings.Development.json
```

Then edit them:

- Set `MSSQL_SA_PASSWORD` in `Backend/Docker/.env` to a strong password.
- In `Backend/appsettings.Development.json`, use that same password in the connection string.
- Set `Jwt:Key` to a random value of at least 32 bytes (e.g. `openssl rand -base64 48`).
- Set `Encryption:MasterKey` to a base64-encoded 32-byte key (e.g. `openssl rand -base64 32`). **Back this key up.** Every stored file is encrypted with a key that is itself encrypted with this one, so losing it means losing every file.

Outside Development, supply the same settings as environment variables (`ConnectionStrings__DefaultConnection`, `Jwt__Key`, `Encryption__MasterKey`). The API refuses to start if any of them is missing.

### How files are stored

Uploaded files are encrypted with AES-256-GCM before they reach disk (`Backend/SecureVaultStorage`, named by GUID). Each file has its own random data key, stored in the database encrypted with the master key. Files are encrypted in 64 KB chunks, so downloads stream without loading the whole file into memory and range requests (e.g. video seeking) still work. A file that has been modified on disk fails to decrypt instead of being served.

### Run Docker in first terminal. 'make sure docker desktop is running'

```bash
cd Backend
cd Docker
docker compose up --build
```

This will start the backend SQL Server container using the password from `Backend/Docker/.env`.



### Run C# API Backend in a second terminal

```bash
cd Backend
dotnet restore
dotnet run
```
This will start the backend api server. 


### Run React in third terminal

```bash 
cd Frontend
npm install
npm run dev
```

## API Documentation

Interactive API docs are served by the API at **http://localhost:3000/swagger**, and the OpenAPI document is at `/swagger/v1/swagger.json` (it can be imported into Postman or Insomnia).

To try protected endpoints, call `POST /user/login`, then click **Authorize** and paste the returned access token. Endpoints are grouped as:

- **Account**: register, login, refresh, logout, profile, password, storage usage, delete account
- **Files**: upload, folders, list, download (single file or zip), rename, move, copy, delete
- **Health**: `/ping` and `/pingsql`


## License

This project is licensed under the MIT License.

## Author

Christian Jones  
[GitHub](https://github.com/ChrisLPJones)
