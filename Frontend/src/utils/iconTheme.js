import { useSyncExternalStore } from "react";

// Icon theme: the style of folder and file icons. Unlike the colour theme this belongs to the account:
// the server stores it (Users.IconTheme) and useUserProfile feeds the loaded value in here.
export const ICON_THEMES = ["default", "windows", "macos", "ubuntu"];

let current = "default";
const listeners = new Set();

export const normaliseIconTheme = (value) => (ICON_THEMES.includes(value) ? value : "default");

export const getIconTheme = () => current;

export const setIconTheme = (value) => {
    const next = normaliseIconTheme(value);
    if (next === current) return;
    current = next;
    listeners.forEach((listener) => listener());
};

const subscribe = (listener) => {
    listeners.add(listener);
    return () => listeners.delete(listener);
};

export const useIconTheme = () => useSyncExternalStore(subscribe, getIconTheme);
