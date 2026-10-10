import { useSyncExternalStore } from "react";
import { jwtDecode } from "jwt-decode";

// The access token lives only in this variable: never in localStorage/sessionStorage, so it is
// gone when the page closes and is renewed from the httpOnly refresh cookie on the next load.
let accessToken = null;
const listeners = new Set();
const notify = () => listeners.forEach((listener) => listener());

// Where "Back to files" returns to; it names a folder, so it goes when the session does
export const LAST_FILES_KEY = "fv-last-files-url";

// Non-secret marker that this browser has a session (a random id, never the token or a user id).
// It tells a new page load whether a refresh is worth trying, and tells other tabs about
// sign-in and sign-out through the storage event. Tampering costs at most one failed refresh.
export const SESSION_HINT_KEY = "fv-session";

// Old versions kept the access token here
const LEGACY_TOKEN_KEY = "token";

export const hasSessionHint = () => {
    try {
        return !!localStorage.getItem(SESSION_HINT_KEY);
    } catch {
        // Storage unavailable: always try the refresh
        return true;
    }
};

const writeSessionHint = (id) => {
    try {
        localStorage.setItem(SESSION_HINT_KEY, id);
    } catch {
        // Storage unavailable: the hint is optional
    }
};

const newSessionId = () => {
    try {
        return crypto.randomUUID();
    } catch {
        return Math.random().toString(36).slice(2) + Date.now().toString(36);
    }
};

// Make sure the hint exists (after a successful refresh); leaves an existing one alone
export const ensureSessionHint = () => {
    if (!hasSessionHint()) writeSessionHint(newSessionId());
};

// One-time migration: drop the long-lived token older versions left in localStorage. Its owner
// still has a refresh cookie, so treat it as a session hint rather than sending them to log in.
try {
    if (localStorage.getItem(LEGACY_TOKEN_KEY) !== null) {
        localStorage.removeItem(LEGACY_TOKEN_KEY);
        ensureSessionHint();
    }
} catch {
    // Storage unavailable: nothing to migrate
}

export const getToken = () => accessToken;

const isReadable = (token) => {
    try {
        jwtDecode(token);
        return true;
    } catch {
        return false;
    }
};

// Keep a server-issued token in memory (an unreadable one is not stored)
export const setToken = (token) => {
    accessToken = token && isReadable(token) ? token : null;
    notify();
};

export const clearToken = () => {
    accessToken = null;
    notify();
    try {
        sessionStorage.removeItem(LAST_FILES_KEY);
    } catch {
        // Storage unavailable: nothing was kept
    }
};

// A new login: keep the token and tell other tabs
export const startSession = (token) => {
    setToken(token);
    writeSessionHint(newSessionId());
};

// Sign out of this browser: drop the token and the hint (other tabs follow)
export const endSession = () => {
    clearToken();
    try {
        localStorage.removeItem(SESSION_HINT_KEY);
    } catch {
        // Storage unavailable: nothing was kept
    }
};

export const subscribeAuth = (listener) => {
    listeners.add(listener);
    return () => listeners.delete(listener);
};

// The access token, re-rendering the component when it changes
export const useAuthToken = () => useSyncExternalStore(subscribeAuth, getToken);

// True if the access token expires within the next 30 seconds (or can't be read)
export const isTokenExpiring = (token) => {
    try {
        const { exp } = jwtDecode(token);
        return !exp || Date.now() >= exp * 1000 - 30_000;
    } catch {
        return true;
    }
};

// True if this tab holds a server-issued access token (even an expired one: the API client
// renews it with the refresh cookie). It says nothing on page load until the session is checked.
export const isAuthenticated = () => accessToken !== null;
