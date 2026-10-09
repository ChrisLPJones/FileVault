# FileVault frontend

React 18 single-page app built with Vite. It talks to the FileVault API through one axios client
(`src/api/api.js`) that attaches the access token and refreshes it from the httpOnly cookie when it
expires.

## Run it

```bash
cp .env.example .env    # VITE_API_BASE_URL: where the API is running
npm install
npm run dev             # http://localhost:5173
```

| Script | What it does |
|---|---|
| `npm run dev` | Development server with hot reload |
| `npm run build` | Production build into `dist/` (the Docker image serves this with nginx) |
| `npm run preview` | Serve the production build locally |
| `npm run lint` | ESLint (CI fails on errors) |
| `npm test` | Vitest and Testing Library tests, run once (CI runs these too); `npm run test:watch` reruns on changes |

`VITE_API_BASE_URL` is compiled into the bundle, so rebuild after changing it. In Docker, nginx serves the
build with a Content-Security-Policy that allows the API's origin, worked out from the same URL
(`nginx/default.conf.template`).

## Where things are

| Path | Contents |
|---|---|
| `src/pages/` | Login (with the two-factor step), Register, password reset and email confirmation, Dashboard (the file manager), Settings, Shared links, the public share page, Admin, and the Layout with the top bar |
| `src/FileManager/` | File manager UI: toolbar, folder tree, file list, details pane, dialogs. Adapted from @cubone/react-file-manager; see [its README](src/FileManager/README.md) |
| `src/api/`, `src/services/` | API calls, all through the shared axios client |
| `src/components/` | Shared pieces: avatar, account menu, file-type icons, buttons, modal |
| `src/contexts/` | File manager state (files, navigation, selection, clipboard, layout, details pane, search, recycle bin) |
| `src/hooks/` | Shared hooks, e.g. `useUserProfile` (one cached load of the user's name and picture) |
| `src/utils/` | Theme and accent colour, password rules, formatting helpers |
| `src/styles/theme.css` | Colour variables for light and dark themes |
