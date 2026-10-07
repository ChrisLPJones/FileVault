# FileVault desktop app

An Electron app that opens your FileVault server in its own window.

It loads the server's web app (for example `http://localhost:5173` from the Docker setup) instead of
bundling a copy, so logins, the refresh-token cookie, CORS and new features behave exactly as in the
browser, and the app never needs updating when the web app changes.

## Run it

```bash
cd Desktop
npm install
npm start
```

On first launch it asks for the server address and checks that a FileVault server answers there.
The address and the window position are saved in the app's user-data folder. **File → Change server…**
switches servers. If the server can't be reached, a screen explains why and offers **Retry**.

## Build an installer

```bash
npm run dist     # Windows: dist/FileVault Setup <version>.exe (NSIS installer)
npm run pack     # unpacked app in dist/win-unpacked, quicker for testing
```

Run on macOS or Linux to build a `.dmg` or `.AppImage`. The installer isn't code-signed, so Windows
SmartScreen shows a warning the first time it runs (**More info → Run anyway**).

## Security

- The web page runs with `contextIsolation`, `sandbox` and no Node.js integration.
- The preload script only exposes its small API (get/save server, retry) to the local connect screen;
  pages loaded from the server get nothing.
- Navigation is limited to the configured server. Other links open in the default browser and new
  windows are blocked.
- Permission requests (camera, microphone, notifications, location, ...) are denied.

## Files

| File | Purpose |
|---|---|
| `main.cjs` | Main process: window, menu, saved settings, navigation rules |
| `preload.cjs` | Connect-screen API (only on the local page) |
| `connect.html`, `connect.css`, `connect.js` | Server address / connection error screen |
| `build/icon.svg` | App icon source; `build/icon.png` (512x512) is rendered from it |

Set `FILEVAULT_DESKTOP_PROFILE` to a folder to use a separate profile (saved server and window
position), for example when testing.
