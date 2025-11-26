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

### Run Docker in first terminal. 'make sure docker desktop is running'

```bash
cd Backend
cd Docker
docker compose up --build
```

This will start the backend SQL Server container with pre-configured settings.



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

All endpoints for uploading, downloading, deleting, and listing files are documented in Postman.



Postman config can be found in `Backend/Postman`. Here's how to import it:

1. Open Postman.
2. Click **"Import"** (top left).
3. Select the file `FileVault.postman_collection.json` from the `Backend/postman` folder.
4. The collection will now appear in your Postman sidebar.

Example endpoints:

- `POST /upload`
- `GET /download/{id}`
- `DELETE /delete/{id}`
- `GET /files`





## License

This project is licensed under the MIT License.

## Author

Christian Jones  
[GitHub](https://github.com/ChrisLPJones)
