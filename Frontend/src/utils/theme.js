import { useSyncExternalStore } from "react";

// Theme preference: "system" follows the OS setting; stored per device.
// index.html applies the stored theme before the app loads, so there's no flash.
const STORAGE_KEY = "fv-theme";
const CHANGE_EVENT = "fv-theme-change";
export const THEME_OPTIONS = ["system", "light", "dark"];

const darkQuery = window.matchMedia("(prefers-color-scheme: dark)");

export const getThemePreference = () => {
    try {
        const stored = localStorage.getItem(STORAGE_KEY);
        return THEME_OPTIONS.includes(stored) ? stored : "system";
    } catch {
        return "system";
    }
};

const resolveTheme = (preference) =>
    preference === "system" ? (darkQuery.matches ? "dark" : "light") : preference;

const applyTheme = () => {
    document.documentElement.dataset.theme = resolveTheme(getThemePreference());
};

export const setThemePreference = (preference) => {
    try {
        localStorage.setItem(STORAGE_KEY, preference);
    } catch {
        // Storage unavailable (e.g. private mode): still switch for this page load
    }
    applyTheme();
    window.dispatchEvent(new Event(CHANGE_EVENT));
};

// Keep the page in sync with OS changes (in "system" mode) and with other tabs
darkQuery.addEventListener("change", () => {
    applyTheme();
    window.dispatchEvent(new Event(CHANGE_EVENT));
});
window.addEventListener("storage", (event) => {
    if (event.key !== STORAGE_KEY) return;
    applyTheme();
    window.dispatchEvent(new Event(CHANGE_EVENT));
});

const subscribe = (callback) => {
    window.addEventListener(CHANGE_EVENT, callback);
    return () => window.removeEventListener(CHANGE_EVENT, callback);
};

// { preference: "system" | "light" | "dark", theme: "light" | "dark" }
export const useTheme = () => {
    const preference = useSyncExternalStore(subscribe, getThemePreference);
    const theme = useSyncExternalStore(subscribe, () => resolveTheme(getThemePreference()));
    return { preference, theme };
};
