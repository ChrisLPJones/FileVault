// FileVault desktop app: a secure window onto a FileVault server's web app.
//
// The app loads the server's frontend URL (e.g. http://localhost:5173 from the Docker stack)
// rather than bundling its own copy, so login cookies, CORS and updates work exactly as in the
// browser. The page gets no Node.js access; only the local connect screen has a small preload API.
const { app, BrowserWindow, Menu, dialog, ipcMain, net, screen, session, shell } = require("electron");
const fs = require("node:fs");
const path = require("node:path");

const DEFAULT_SERVER = "http://localhost:5173";

// Optional separate profile (saved server and window position), e.g. for testing
if (process.env.FILEVAULT_DESKTOP_PROFILE) app.setPath("userData", process.env.FILEVAULT_DESKTOP_PROFILE);
const CONFIG_FILE = () => path.join(app.getPath("userData"), "config.json");

// ---------- settings (server URL and window position) ----------

function readConfig() {
  try {
    return JSON.parse(fs.readFileSync(CONFIG_FILE(), "utf8"));
  } catch {
    return {};
  }
}

function writeConfig(changes) {
  const config = { ...readConfig(), ...changes };
  fs.mkdirSync(path.dirname(CONFIG_FILE()), { recursive: true });
  fs.writeFileSync(CONFIG_FILE(), JSON.stringify(config, null, 2));
  return config;
}

// http(s) URLs only; returns the origin-normalised URL or null
function normaliseServerUrl(input) {
  try {
    const url = new URL(String(input).trim());
    if (url.protocol !== "http:" && url.protocol !== "https:") return null;
    url.hash = "";
    url.search = "";
    return url.toString().replace(/\/+$/, "");
  } catch {
    return null;
  }
}

// ---------- window ----------

let mainWindow = null;

function serverOrigin() {
  const server = readConfig().serverUrl;
  return server ? new URL(server).origin : null;
}

function showConnectScreen(message) {
  const query = {
    server: readConfig().serverUrl ?? DEFAULT_SERVER,
    ...(message ? { message } : {}),
  };
  mainWindow.loadFile(path.join(__dirname, "connect.html"), { query });
}

function openServer() {
  const server = readConfig().serverUrl;
  if (!server) return showConnectScreen();
  mainWindow.loadURL(`${server}/dashboard`);
}

// Saved bounds, unless that spot is no longer on any connected display (e.g. a monitor was unplugged)
function restorableBounds() {
  const { bounds } = readConfig();
  if (!bounds) return undefined;

  const onScreen = screen.getAllDisplays().some(({ workArea: a }) =>
    bounds.x < a.x + a.width && bounds.x + bounds.width > a.x &&
    bounds.y < a.y + a.height && bounds.y + 40 > a.y); // title bar must be reachable
  return onScreen ? bounds : { width: bounds.width, height: bounds.height };
}

function createWindow() {
  const bounds = restorableBounds();

  mainWindow = new BrowserWindow({
    width: bounds?.width ?? 1280,
    height: bounds?.height ?? 820,
    x: bounds?.x,
    y: bounds?.y,
    minWidth: 720,
    minHeight: 500,
    title: "FileVault",
    icon: path.join(__dirname, "build", "icon.png"),
    backgroundColor: "#121316",
    show: false,
    webPreferences: {
      preload: path.join(__dirname, "preload.cjs"),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
      spellcheck: false,
    },
  });

  mainWindow.once("ready-to-show", () => mainWindow.show());

  // Remember size and position between runs
  const saveBounds = () => {
    if (!mainWindow.isMaximized() && !mainWindow.isMinimized()) writeConfig({ bounds: mainWindow.getBounds() });
  };
  mainWindow.on("resize", saveBounds);
  mainWindow.on("move", saveBounds);

  // Links to other sites open in the default browser; nothing opens new app windows
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    openExternally(url);
    return { action: "deny" };
  });

  // Stay on the FileVault server (or the local connect screen)
  mainWindow.webContents.on("will-navigate", (event, url) => {
    if (url.startsWith("file:")) return;
    if (new URL(url).origin === serverOrigin()) return;
    event.preventDefault();
    openExternally(url);
  });

  // Server unreachable: show the connect screen with the reason and a retry button
  mainWindow.webContents.on("did-fail-load", (_event, errorCode, errorDescription, validatedUrl, isMainFrame) => {
    if (!isMainFrame || errorCode === -3 /* aborted, e.g. a redirect */ || validatedUrl.startsWith("file:")) return;
    showConnectScreen(`Couldn't reach ${readConfig().serverUrl} (${errorDescription}).`);
  });

  openServer();
}

function openExternally(url) {
  try {
    const { protocol } = new URL(url);
    if (protocol === "http:" || protocol === "https:" || protocol === "mailto:") shell.openExternal(url);
  } catch {
    // ignore malformed URLs
  }
}

// ---------- connect screen API (see preload.cjs) ----------

ipcMain.handle("server:get", () => readConfig().serverUrl ?? DEFAULT_SERVER);

ipcMain.handle("server:connect", async (_event, input) => {
  const server = normaliseServerUrl(input);
  if (!server) return { ok: false, error: "Enter a full address starting with http:// or https://" };

  try {
    const response = await net.fetch(server, { cache: "no-store" });
    const html = await response.text();
    if (!response.ok || !html.includes('id="root"')) {
      return { ok: false, error: `${server} answered, but it doesn't look like a FileVault server.` };
    }
  } catch (err) {
    return { ok: false, error: `Couldn't reach ${server}: ${err.message}` };
  }

  writeConfig({ serverUrl: server });
  openServer();
  return { ok: true };
});

ipcMain.handle("server:retry", () => openServer());

// ---------- menu ----------

function buildMenu() {
  const isMac = process.platform === "darwin";
  const template = [
    ...(isMac ? [{ role: "appMenu" }] : []),
    {
      label: "File",
      submenu: [
        { label: "Change server…", click: () => showConnectScreen() },
        { label: "Open in browser", click: () => readConfig().serverUrl && shell.openExternal(readConfig().serverUrl) },
        { type: "separator" },
        isMac ? { role: "close" } : { role: "quit" },
      ],
    },
    { role: "editMenu" },
    {
      label: "View",
      submenu: [
        { role: "reload" },
        { type: "separator" },
        { role: "resetZoom" },
        { role: "zoomIn" },
        { role: "zoomOut" },
        { type: "separator" },
        { role: "togglefullscreen" },
        ...(app.isPackaged ? [] : [{ role: "toggleDevTools" }]),
      ],
    },
    {
      role: "help",
      submenu: [
        {
          label: "About FileVault",
          click: () =>
            dialog.showMessageBox(mainWindow, {
              type: "info",
              title: "About FileVault",
              message: `FileVault ${app.getVersion()}`,
              detail: `Connected to: ${readConfig().serverUrl ?? "no server yet"}\nElectron ${process.versions.electron}`,
            }),
        },
      ],
    },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

// ---------- app lifecycle ----------

// One window only: a second launch focuses the existing one
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on("second-instance", () => {
    if (!mainWindow) return;
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.focus();
  });

  app.whenReady().then(() => {
    // The page never needs camera, microphone, notifications, location, etc.
    session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false));

    buildMenu();
    createWindow();

    app.on("activate", () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow();
    });
  });

  app.on("window-all-closed", () => {
    if (process.platform !== "darwin") app.quit();
  });
}
