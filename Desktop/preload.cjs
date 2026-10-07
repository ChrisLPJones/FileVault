// Exposes a tiny API to the local connect screen only. Pages loaded from the server get nothing.
const { contextBridge, ipcRenderer } = require("electron");

if (location.protocol === "file:") {
  contextBridge.exposeInMainWorld("desktop", {
    getServer: () => ipcRenderer.invoke("server:get"),
    connect: (url) => ipcRenderer.invoke("server:connect", url),
    retry: () => ipcRenderer.invoke("server:retry"),
  });
}
