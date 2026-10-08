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

`VITE_API_BASE_URL` is compiled into the bundle, so rebuild after changing it.

## Where things are

| Path | Contents |
|---|---|
| `src/pages/` | Login, Register, Dashboard (the file manager), Settings, and the Layout with the top bar |
| `src/FileManager/` | File manager UI: toolbar, folder tree, file list, details pane, dialogs. Adapted from @cubone/react-file-manager; see [its README](src/FileManager/README.md) |
| `src/api/`, `src/services/` | API calls, all through the shared axios client |
| `src/components/` | Shared pieces: avatar, account menu, file-type icons, buttons, modal |
| `src/contexts/` | File manager state (files, navigation, selection, clipboard, layout, details pane) |
| `src/hooks/` | Shared hooks, e.g. `useUserProfile` (one cached load of the user's name and picture) |
| `src/utils/` | Theme and accent colour, password rules, formatting helpers |
| `src/styles/theme.css` | Colour variables for light and dark themes |
