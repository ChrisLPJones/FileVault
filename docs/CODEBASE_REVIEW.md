# FileVault — Codebase Review

**Date:** 2026-10-06
**Scope:** Every tracked file in `Backend/`, `Backend.Test/`, `Frontend/src/`, Docker, docs and root config.

**What I ran:**
- `dotnet build FileVault.sln`: builds with 0 errors and 0 warnings.
- `npx eslint .` in `Frontend/`: **70 problems (50 errors, 20 warnings)**.
- I did **not** run the xUnit tests. They need a live SQL Server and they write to it (see §5).

---

## TL;DR — the top 10

| # | Severity | Issue | Where |
|---|----------|-------|-------|
| 1 | 🔴 Security | JWT signing key, SA password and a seeded admin password hash are committed to a public repo | `Backend/appsettings.json:10-14`, `Backend/Docker/docker-compose.yml:6,24`, `Backend/Docker/db/init.sql:48-58` |
| 2 | 🔴 Security | `GetFolderById` doesn't filter by `UserId`, so a user can upload into or create folders under another user's folder GUID | `Backend/Services/DatabaseService.cs:180-203` |
| 3 | 🔴 Feature gap | The README says files are encrypted on disk, but they are stored as plaintext | `Backend/Services/FileServices.cs:29-30` |
| 4 | 🔴 Bug | Deleting a folder removes the DB rows for its children (via trigger) but never deletes their files from disk, so storage leaks forever | `Backend/Routes/FileRoutes.cs:168-178`, `init.sql:117-137` |
| 5 | 🔴 Bug | Bulk delete crashes with a 500 if you select a folder *and* something inside it, because `IsFileAsync` throws on the already-deleted child | `Backend/Routes/FileRoutes.cs:170`, `DatabaseService.cs:146-147` |
| 6 | 🔴 Bug | The frontend calls `/rename`, `/copy` and `/move`, but none of them exist on the backend. Axios throws and the loader stays stuck on screen | `Frontend/src/api/renameAPI.js`, `fileTransferAPI.js`, `Dashboard.jsx:77-123` |
| 7 | 🟠 Bug | Upload hides DB errors: `AddFile` catches `SqlException` and just logs it, so the API returns "File Uploaded" while the file on disk has no metadata | `DatabaseService.cs:72-80` |
| 8 | 🟠 Bug | The "Download" button on the preview screen requests `/download/{name}` but the API expects the GUID, so it always fails | `PreviewFile.action.jsx:83` |
| 9 | 🟠 Build | `import "./register.css"` but the file is `Register.css`. This works on Windows but breaks the build on Linux/Docker/CI | `Frontend/src/pages/Register/Register.jsx:2` |
| 10 | 🟠 Tests | The integration tests download and delete by filename (`/download/test.txt`), but the API now takes GUIDs, so tests 7–8 fail. Tests also write to the real dev DB | `Backend.Test/FileEndpoints.Test.cs:147,159` |

---

## 1. Security issues (fix first)

### 1.1 Secrets committed to source control 🔴
- `Backend/appsettings.json:14` contains the real HMAC key for JWTs. Anyone who clones the repo can forge a token for any user ID.
- `appsettings.json:10` and `docker-compose.yml:6,24` contain the `sa` password.
- `init.sql:48-58` seeds an `admin` user with a fixed bcrypt hash. Anyone who can crack or guess that password has admin access on every deployment.

**Fix**
- Move secrets into `dotnet user-secrets` for dev and environment variables for Docker/prod. Keep only placeholders in `appsettings.json` and add an `appsettings.Development.json.example`.
- **Rotate the JWT key.** It's in git history, so deleting it from the file isn't enough.
- Use an `.env` file (gitignored) for compose variables, e.g. `MSSQL_SA_PASSWORD: ${SA_PASSWORD}`.
- Remove the seeded admin, or seed it from an env var on first run.
- Don't connect as `sa`. Create a least-privilege app login in `init.sql`.
- Fail fast at startup if `Jwt:Key` is missing or shorter than 32 bytes (`Program.cs:36` currently throws an unhelpful NRE).

### 1.2 Cross-tenant folder access (IDOR) 🔴
`DatabaseServices.GetFolderById` (`DatabaseService.cs:185`) queries `WHERE GUID = @GUID` with no `UserId` filter. It's used by:
- `FileServices.UploadFile` (`FileServices.cs:19`): a file can be uploaded into another user's folder. It inherits their `Path` and `ParentId`, so if they delete that folder, the trigger deletes *your* row too.
- `FileServices.CreateFolder` (`FileServices.cs:62`): same problem for folders.

GUIDs are hard to guess, which lowers the risk, but it's still a broken access check. **Fix:** add `AND UserId = @UserId` and pass `userId` in. Also confirm the parent row has `IsDirectory = 1`.

### 1.3 Other auth / security gaps 🟠
| Issue | Location | Recommendation |
|---|---|---|
| No refresh token, logout or revocation (TODOs only) | `AuthRoutes.cs:234`, `AuthServices.cs:78-80` | Add refresh tokens in an httpOnly cookie, `/user/refresh` and `/user/logout`, and a token store or `jti` denylist |
| JWT stored in `localStorage`, readable by any XSS | `Login.jsx:44`, `api.js:9` | Move to httpOnly + SameSite cookies, or at least keep the access token short-lived and in memory |
| No password policy on the server (the frontend shows rules but only enforces length ≥ 6) | `AuthRoutes.cs:38-42`, `Register.jsx:46-62` | Validate on the server (length, complexity, max length ~72 for bcrypt) and make the frontend enforce the rules it displays |
| `PUT /user` changes the password without asking for the current one, and requires all three fields | `AuthRoutes.cs:163-177` | Separate endpoints: `PATCH /user/profile` and `POST /user/password` (which requires `currentPassword`) |
| `PUT /user` doesn't lowercase or validate the new email, and returns raw `ex.Message` (SQL details) on a unique-constraint failure | `DatabaseService.cs:334-363` | Normalize email, check uniqueness first, return a generic error |
| JWT `email` claim goes stale after an email change | `AuthServices.cs:42` | Reissue the token after a profile update |
| No rate limiting or lockout on `/user/login` and `/user/register` | `Program.cs` | `builder.Services.AddRateLimiter(...)` (built into .NET 8) |
| Error responses leak internals (`$"Upload failed: {ex.Message}"`, `$"{ex.Message}"`) | `FileRoutes.cs:50,80`, `FileServices.cs:47,80` | Log the exception and return a generic message |
| No HTTPS redirection or HSTS; API runs on plain `http://localhost:3000` | `Program.cs`, `launchSettings.json` | `app.UseHttpsRedirection()` and `UseHsts()` outside Development |
| CORS origin hard-coded to `http://localhost:5173` (and Electron will need its own origin) | `Program.cs:22` | Read allowed origins from config |
| `/pingsql` is anonymous and polled every 3s from the login page | `HealthCheckRoutes.cs:15`, `ServerStatus.jsx:41` | Use `AddHealthChecks().AddSqlServer()`, expose only `/health`, and poll less often |
| No upload size limit or file-type checks on the server (the 10 MB limit and allow-list exist only in the frontend) | `FileRoutes.cs:20-52`, `Dashboard.jsx:183-185` | Set `RequestSizeLimit` / `FormOptions.MultipartBodyLengthLimit`, and enforce per-user quotas (see §3) |
| `Role` column exists but nothing uses it | `init.sql:35` | Put the role in the JWT claim and add `RequireAuthorization("Admin")` policies, or remove the column |

---

## 2. Backend bugs and correctness problems

### 2.1 Database schema (`Backend/Docker/db/init.sql`)
- **The self-referencing FK with `ON DELETE CASCADE` (lines 90-93, 103-106) will be rejected by SQL Server** with error 1785 ("may cause cycles or multiple cascade paths"). In practice, `ParentId` has no FK, and the recursive trigger is what actually does the cascading. Pick one approach: drop the cascade and keep the trigger, or use an `INSTEAD OF DELETE` trigger.
- `GUID` and `ParentId` are `NVARCHAR(100)`. Use `UNIQUEIDENTIFIER`, matching `Users.Id`.
- `UpdatedAt` is set on insert but never updated. There's also no `CreatedAt` on `Files`.
- No index on `Files(UserId)` or `Files(ParentId)`. Every listing does a table scan.
- `FilePath` is stored for every row (denormalized). Renaming or moving a folder means rewriting the path of every descendant. Either derive paths from `ParentId` with a recursive CTE, or make sure every rename/move updates descendants in one transaction.
- No uniqueness on `(UserId, ParentId, FileName)`, so duplicate names can exist in the same folder. The frontend navigates **by path**, so duplicates cause collisions there.
- There are no migrations, just a one-shot script. Consider EF Core migrations, DbUp, or numbered SQL scripts.

### 2.2 File routes and services
| # | Bug | Location |
|---|---|---|
| a | `file.ContentType` is read **before** the null check, so a request without a file throws NRE, which surfaces as "Upload failed: Object reference…" | `FileRoutes.cs:35-39` |
| b | `UploadFile` with an invalid `parentId`: `folder` is null, so `folder.Path` throws NRE | `FileServices.cs:19-20` |
| c | Root uploads store `ParentId = ""` while root folders store `NULL`. This is inconsistent, and would violate the FK if it existed | `FileRoutes.cs:34`, `DatabaseService.cs:69` vs `126` |
| d | `AddFile` swallows `SqlException`, so the upload reports success, the file is on disk, and there's no metadata (orphaned file) | `DatabaseService.cs:72-80` |
| e | `DeleteFileMetadata` also swallows errors | `DatabaseService.cs:168-176` |
| f | Deleting a folder (`DeleteFileMetadata`) removes descendant rows via trigger but **never deletes their files on disk** | `FileRoutes.cs:174-177` |
| g | Bulk delete: `IsFileAsync` throws `InvalidOperationException` when the item is gone (e.g. its parent was deleted earlier in the same loop), resulting in an unhandled 500 | `FileRoutes.cs:168-178` |
| h | Bulk delete ignores each `DeleteFile` result and always returns "Deleted Successfully" | `FileRoutes.cs:172,180` |
| i | `DeleteFile` refuses to delete metadata when the file is missing on disk, so "ghost" rows can never be removed | `FileServices.cs:115-116` |
| j | `DeleteFile` deletes metadata first, then the file. If `File.Delete` fails, the file is orphaned. Better: move the file to a pending-delete location, delete metadata, then delete the file | `FileServices.cs:121-122` |
| k | `GET /download/{fileName}`: the parameter is really a GUID. `FileName` in the result is set to the GUID, so `Content-Disposition` gives the user `a1b2c3…` instead of the real name | `FileRoutes.cs:103`, `FileServices.cs:89-101` |
| l | Download makes 4+ DB round trips (`CheckConnection`, `GetFileGUIDAsync`, `GetMimeType`, which calls `GetFileGUIDAsync` again) | `FileServices.cs:90-92`, `DatabaseService.cs:84-106` |
| m | Download reads the **whole file into memory** (`ReadAllBytesAsync`). Large files will exhaust RAM, and there's no range support, so video can't seek | `FileServices.cs:100` |
| n | Upload returns `{ success: "File Uploaded: x" }` rather than the created file object, so the frontend has to refetch everything | `FileRoutes.cs:44-46` |
| o | `CreateFolder` doesn't validate the name (`/`, `..`, empty after trim, length) or check for duplicates. A `/` in a name breaks path navigation | `FileServices.cs:69` |
| p | `GET /files` is synchronous (`connection.Open()`, `ExecuteReader`) and returns no `parentId` or `mimeType` | `DatabaseService.cs:209-237` |
| q | Path sanitisation: `Path.GetFileName(fileId)` is used on GUIDs. That's harmless, but validate the ID with `Guid.TryParse` instead | `FileServices.cs:89,107` |

### 2.3 Auth routes
- `/user/register` and `/user/login` manually read and deserialize the body. Bind `UserModel` / `LoginModel` directly like `PUT /user` does.
- `/user/login` validation error uses `Error` (capital E) while the other routes use `error`. Make the error shape consistent (ideally RFC 7807 `Results.Problem` / `ValidationProblem`).
- `/user/info`, `PUT` and `DELETE /user` don't check that `userId` is non-null.
- `DELETE /user` returns **400** for "User not found". It should be 404.
- `UpdateUserLastLogin` matches by the email the user typed rather than by `Id`.

### 2.4 Code quality (backend)
- `Nullable` is disabled in `Backend.csproj`. Enable it; it would have caught §2.2a/b.
- `Console.WriteLine` is used everywhere for logging, and `System.Console.WriteLine(file)` is left in `FileServices.cs:24`. Inject `ILogger<T>`.
- Every route repeats the same `try/catch` and `user.FindFirst(...)`. Add `app.UseExceptionHandler()` with `IProblemDetailsService`, and a `ClaimsPrincipal.GetUserId()` extension method.
- Unused or odd models:
  - `EmailLoginModel` and `UserLoginModel` are never used.
  - `IdList.cs` declares a class named `IsList` (typo).
  - `FolderModel` uses `Microsoft.VisualBasic.DateAndTime` as a date type (should be `DateTime`) and has a Mongo-style `__v` field.
  - `HttpReturnResult` has leftover `// <-- Add this property` comments.
- Unused `using`s: `System.Runtime.Intrinsics.Arm`, `System.Net.WebSockets`, `Microsoft.AspNetCore.Identity`, `Microsoft.VisualBasic`.
- Unnecessary package references: `System.Net.Http`, `System.Text.Json` and `System.Text.RegularExpressions` are already part of .NET 8. Remove them from both csproj files.
- `Backend/Backend.sln` duplicates the root `FileVault.sln`. Keep one.
- Services take `DatabaseServices db` as a **method parameter** instead of through constructor DI.
- Raw ADO.NET with `AddWithValue` everywhere. Consider Dapper (low effort) or EF Core, plus a repository interface so services can be unit-tested.
- There's no Swagger/OpenAPI. `AddEndpointsApiExplorer()` + `AddSwaggerGen()` would replace the out-of-date Postman collection.

---

## 3. Missing functionality

Ordered roughly by how much each item matters to the app as described in the README ("secure file-sharing").

### Backend features the frontend already expects
1. **Rename:** `PATCH /rename { id, newName }`. Must update `FilePath` for every descendant when renaming a folder, and reject duplicates.
2. **Move:** `PUT /move { sourceIds, destinationId }`. Update `ParentId` and paths, and block moving a folder into itself or a descendant.
3. **Copy:** `POST /copy { sourceIds, destinationId }`. Copy blobs (new GUIDs) and metadata recursively.
4. **Multi-file and folder download:** `POST /download/zip { ids }` that streams a `ZipArchive`. This fixes the "multiple file download issue" in `todo.md`, because browsers block repeated programmatic downloads.

### Core features claimed but missing
5. **Encryption at rest** (README and `Backend/TODO.md`). Suggested approach:
   - AES-256-GCM per file, with a random data key per file.
   - Wrap the data key with a master key from config/Key Vault, and store the wrapped key and nonce in `Files`.
   - Encrypt and decrypt as a stream so large files aren't loaded into memory.
6. **Sharing.** The app is described as "file-sharing", but there's no sharing at all. Minimum:
   - Expiring share links (`POST /share { fileId, expiresAt, password? }` and an anonymous `GET /s/{token}`).
   - Optionally, sharing with other registered users (a `Shares` table with read/write permissions).
7. **Storage quotas** (`Backend/TODO.md`): a `StorageQuota` column on `Users`, checks before upload, and `GET /user/usage` for a usage bar in the UI.
8. **Refresh token and logout** (see §1.3).
9. **Dockerized deployment** (README claims it). There's no Dockerfile for the API or the frontend; compose only runs SQL Server.

### Account features
10. A **settings UI** (change username/email/password, delete account). The endpoints exist but there's no page for them.
11. **Password reset** via email, and **email verification** on register.
12. Show the logged-in user (`/user/info`) in the header. Hide Login/Register when logged in and Logout when logged out (`Layout.jsx`).
13. User avatar (`Backend/TODO.md`).

### Nice-to-have file features
14. Search (by name, type, date) and server-side sorting/paging for large libraries.
15. Recycle bin / soft delete with restore.
16. Chunked or resumable uploads for large files, with server-side size limits.
17. Thumbnails for images and PDFs, and preview for more types (the preview supports mp4/mp3, but upload `acceptedFileTypes` doesn't allow them).
18. File versioning.
19. Audit log (who uploaded, downloaded or deleted what, and when).
20. Admin dashboard (users, storage usage), which would justify the `Role` column.
21. Dark-mode toggle and Electron app (`todo.md`). Note: `Frontend/electron` exists locally but isn't committed.

---

## 4. Frontend issues

### 4.1 Functional bugs
| # | Bug | Location |
|---|---|---|
| a | Rename, copy and move call nonexistent endpoints. `renameAPI` and `moveItemAPI` don't catch errors, so `handleRename` and `handlePaste` throw before `setIsLoading(false)` and **the loader stays on screen** | `Dashboard.jsx:77-123`, `renameAPI.js`, `fileTransferAPI.js` |
| b | `handleDelete` has no try/catch (axios throws on non-2xx), which leaves the loader stuck | `Dashboard.jsx:90-100`, `deleteAPI.js` |
| c | Rename removes the item from the list **before** the API call succeeds (see the TODO in the code), so it vanishes on failure | `Rename.action.jsx:107` |
| d | The preview's "Download" button uses `selectedFiles[0].name` instead of `_id` | `PreviewFile.action.jsx:83` |
| e | Preview blob URL cleanup uses a stale `fileURL` (always `null`), so object URLs are never revoked (memory leak) | `PreviewFile.action.jsx:77` |
| f | `handleFileUploaded` pushes `{ success: "File Uploaded: …" }` into `files` (not a file object, which fails propTypes) and then refetches anyway | `Dashboard.jsx:69-73` |
| g | Case-sensitive import: `./register.css` vs `Register.css` | `Register.jsx:2` |
| h | `/` (index) renders `<Login />` without `PublicRoute`, so a logged-in user still sees the login form | `main.jsx:19` |
| i | `ProtectedRoute` only checks that a token exists, not that it hasn't expired. That logic is duplicated separately in `useAuthCheck`. There's also no 401 response interceptor, so an expired session fails silently | `ProtectedRoute.jsx`, `useAuthCheck.jsx`, `api.js` |
| j | Register shows password rules (number, uppercase) but `validateForm` doesn't enforce them. `passwordValid` is computed and never used | `Register.jsx:17-28,46-62` |
| k | Login sets "Login Successful" after `navigate()`, so the message is never seen | `Login.jsx:45-46` |
| l | Upload extension check uses `acceptedFileTypes.includes(ext)`, a substring match: `"doc"` matches because of `.docx`, and files with no extension behave oddly. `.exe` is in the allow-list | `UploadFile.action.jsx:37`, `Dashboard.jsx:185` |
| m | Multi-file download fetches each file as a blob sequentially and fires clicks one after another. Browsers block this, and everything is buffered in memory | `downloadFileAPI.js` |
| n | Stray template literal on its own line, ` `` ;`, after the router definition | `main.jsx:47` |

### 4.2 Configuration
- Three env vars point at the same API: `VITE_API_URL` (Auth, ServerStatus, Preview), `VITE_API_BASE_URL` (api.js, downloads, upload) and `VITE_API_FILES_BASE_URL` (unused by the custom preview). Consolidate to **one**, and send every request through the shared `api` axios instance.
- There's no `.env.example`. A new clone can't run the frontend without guessing the variable names.
- `getAllFilesAPI` logs the full file listing to the console on every load.
- All 20 locale JSON files are bundled eagerly but the language is hard-coded to `en-US`. Lazy-load them, or remove the unused ones.

### 4.3 Code quality
- ESLint reports **50 errors and 20 warnings**: unused variables, `set-state-in-effect` in `Dashboard.jsx` and `Login.jsx`, `no-case-declarations` in `sortFiles.js`, and others.
- Several `console.log` handlers remain in production code (`handleCut`, `handleCopy`, `handleSelectionChange`, `handleLayoutChange`, `handleFileOpen`).
- `Dashboard.jsx` names its component `App`.
- The vendored file manager (`src/FileManager`, `contexts`, `components`) looks adapted from `@cubone/react-file-manager`. Credit it and check its licence. If you've only changed a little, consider depending on the package instead.
- Mixed styling: react-bootstrap (only used for one `Alert`), plain CSS and SCSS. Drop react-bootstrap or use it consistently.
- `Frontend/README.md` is still the Vite template.
- `ServerStatus` makes 2 requests every 3 seconds per open tab.

---

## 5. Tests

- **The integration tests are broken.**
  - `DownloadFile_ShouldReturnContent` and `DeleteFile_ShouldSucceed` use `/download/test.txt` and `/delete/test.txt`, but the API now resolves by GUID. They need to read the uploaded file's `_id` from `/files` first.
- **The tests mutate the real dev database.** They use `WebApplicationFactory<Program>` with the normal `appsettings.json`.
  - Use Testcontainers (`Testcontainers.MsSql`) or a dedicated test DB via `WithWebHostBuilder` config overrides.
  - Use a temporary `StorageRoot`.
- **The tests can't be re-run.** `RegisterUser_ShouldSucceed` fails if a previous run died before `DeleteUser`. Use unique emails per run plus cleanup in `IAsyncLifetime`.
- The tests depend on ordering through a custom orderer and `static _jwt`. That's fragile; each test should arrange its own state.
- **Untested:** folders, bulk delete, `/user` update and delete edge cases, auth failures (401s), cross-user access (the IDOR in §1.2), and invalid input.
- There are **no unit tests**. Extracting interfaces such as `IFileStorage` and `IFileRepository` would let `FileServices` be unit-tested.
- There are **no frontend tests**. Add Vitest + React Testing Library for login/register validation and the Dashboard handlers.
- There's **no CI**. Add a GitHub Actions workflow: `dotnet build` + `dotnet test` (with a SQL service container) and `npm ci && npm run lint && npm run build`.
- `Backend.Test.csproj` pins old `xunit` 2.5.3 and `Microsoft.AspNetCore.Mvc.Testing` 8.0.5, and repeats the unnecessary package references.

---

## 6. DevOps, docs and repo hygiene

- **Docker**
  - The `mcr.microsoft.com/mssql-tools` image and `/opt/mssql-tools/bin/sqlcmd` are deprecated. Use `mssql-tools18` (needs `-C`) or `sqlcmd` from the SQL Server 2022 image.
  - `sleep 20` is a race condition. Add a `healthcheck` on `sqlserver` and `depends_on: condition: service_healthy`.
  - `SA_PASSWORD` is deprecated; the variable is now `MSSQL_SA_PASSWORD`.
  - Add `Dockerfile`s for the API (multi-stage, `mcr.microsoft.com/dotnet/aspnet:8.0`) and the frontend (Vite build served by nginx).
  - Move `docker-compose.yml` to the repo root so `docker compose up` starts everything, with a named volume for `SecureVaultStorage`.
- **README**
  - It claims encryption and Docker support that don't exist.
  - The "Example endpoints" list is incomplete: it's missing `/folder`, bulk `/delete` and `/user/*`.
  - It doesn't mention the frontend `.env`.
  - It says MIT, but there's **no `LICENSE` file**.
- **Postman collection** is outdated: it uses filenames for download/delete and has no `/folder` or bulk delete. Replace it with Swagger, or regenerate it.
- **Root `package.json` / `package-lock.json`** contain stray dependencies (`localforage`, `match-sorter`, `sort-by`, leftovers from the React Router tutorial). Delete them.
- **`.vscode/`** is tracked even though `.gitignore` lists it.
- **TODO lists** exist in three places (`todo.md`, `Backend/TODO.md`, code comments). Move them to GitHub Issues.
- **.NET 8** reaches end of support in November 2026. Plan an upgrade to .NET 10 (LTS).

---

## 7. Suggested order of work

**Phase 1: Security and data integrity (1–2 days)**
1. Remove secrets, rotate the JWT key, add a config template and startup validation (§1.1).
2. Scope `GetFolderById` to the user (§1.2).
3. Fix upload NREs, swallowed SQL errors and `ParentId` NULL vs "" (§2.2 a–e).
4. Fix folder delete so it removes child files from disk; make bulk delete robust (§2.2 f–i).
5. Fix the `register.css` import (§4.1g).

**Phase 2: Make the existing UI work end to end (2–4 days)**

6. Implement `/rename`, `/move` and `/copy`, with descendant path updates (§3.1–3).
7. Stream downloads with the correct filename and range support; add zip download (§2.2 k–m, §3.4).
8. Frontend error handling: try/catch in every API call, a 401 interceptor and one env var (§4.1, §4.2).
9. Fix the integration tests and add CI (§5).

**Phase 3: Deliver what the README promises (1–2 weeks)**

10. Encryption at rest (§3.5).
11. Refresh tokens and logout; server-side password policy; rate limiting (§1.3).
12. Full Docker setup: API and frontend Dockerfiles, root compose with healthchecks (§6).
13. Storage quotas and an account settings page (§3.7, §3.10).

**Phase 4: Portfolio polish**

14. Sharing links (§3.6), search, recycle bin, thumbnails.
15. Swagger, a rewritten README with screenshots, a LICENSE file, and ESLint at zero errors.
