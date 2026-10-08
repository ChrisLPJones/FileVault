# Roadmap

What's planned next, roughly in priority order. Everything from the original
[codebase review](CODEBASE_REVIEW.md) phases 1–3 is done; this list picks up from there.

## 1. Core features

| Feature | What it involves |
|---|---|
| **Share links** | Create a link to a file or folder, optionally with an expiry date and password; a public page to view/download it; list and revoke your links. New `Shares` table and endpoints, served without login but rate-limited |
| **Search** | Find files and folders by name across every folder, from a search box in the top bar. The full file list is already loaded, so this can start client-side |
| **Recycle bin** | Deleting moves items to a bin instead of removing them; restore or empty it; items are purged automatically after 30 days. Deleted items keep counting towards the quota until purged |
| **Forgot password / email verification** | Reset links sent by email, and confirming the address on sign-up. Needs SMTP settings and a token table |
| **Bigger uploads** | Chunked, resumable uploads so files above the 100 MB limit work and a dropped connection doesn't restart the upload; uploading whole folders by drag-and-drop |

## 2. Security

| Feature | What it involves |
|---|---|
| **Two-factor authentication** | TOTP codes (authenticator apps) with recovery codes, set up from Settings |
| **Active sessions** | List the devices you're logged in on (each has its own refresh token already) and sign any of them out |
| **Production hardening** | Swagger only in Development (or behind a setting), security headers (CSP, HSTS) in nginx, and a guide for hosting behind HTTPS |

## 3. Polish

| Feature | What it involves |
|---|---|
| **Image thumbnails** | Small previews instead of icons in grid view, generated on upload and cached |
| **Favourites and recent files** | Star items and see recently opened ones in the folder tree |
| **Mobile layout** | Usable on phones: collapsible panes, larger touch targets |
| **Frontend tests** | Vitest and Testing Library for the file manager, run in CI |
| **README screenshots** | Screenshots or a short demo GIF of the main screens |
| **Admin page** | For the server owner: list users, change quotas, see total storage |
