# Roadmap

## Done

Everything from the original [codebase review](CODEBASE_REVIEW.md) phases 1–3, and the three
groups of features that followed:

| Area | Features |
|---|---|
| **Core** | Share links (expiry, password, a Shared links page), search, recycle bin, email confirmation and password reset, chunked resumable uploads and folder uploads, every file type allowed (2 GB per file by default) |
| **Security** | Two-factor authentication with recovery codes, active sessions, Swagger off in production, Content-Security-Policy and security headers, a [deployment guide](DEPLOYMENT.md) for hosting behind HTTPS |
| **Polish** | Image thumbnails, favourites and recently opened files, phone and tablet layout, frontend tests in CI, README screenshots, an admin page for quotas and storage |

## Next

Ideas for what could come next, roughly in priority order. None of them has been started.

| Feature | What it involves |
|---|---|
| **Master key rotation** | A command that re-encrypts every file's key, and the stored share link and authenticator secrets, under a new `ENCRYPTION_MASTER_KEY`. Files themselves don't need re-encrypting, since only their keys are wrapped with the master key. Mainly for when the key may have leaked |
| **Sharing with other accounts** | Share a folder with another FileVault user, read-only or with edit rights, instead of only by public link |
| **File versions** | Keep earlier versions when a file is uploaded again under the same name, and restore one |
| **More previews and thumbnails** | Thumbnails for videos and PDFs; rendered Markdown in the details pane |
| **Activity log** | When someone downloaded a shared link, logged in from a new device or changed security settings, shown in Settings |
| **Admin tools** | Disable or delete accounts, and see storage use over time |
| **Releases** | Versioned Docker images on GitHub Container Registry and desktop app installers on GitHub Releases, built by CI from a tag |
