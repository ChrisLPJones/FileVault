import axios from "axios";
import {
    SESSION_HINT_KEY,
    clearToken,
    endSession,
    ensureSessionHint,
    getToken,
    hasSessionHint,
    isTokenExpiring,
    setToken,
} from "../utils/auth";

export const API_BASE_URL = import.meta.env.VITE_API_BASE_URL;

export const api = axios.create({
    baseURL: API_BASE_URL,
    // Send the httpOnly refresh-token cookie to /user/refresh and /user/logout
    withCredentials: true,
});

// One refresh at a time, shared by every request that needs it
let refreshPromise = null;

// Tabs share one refresh cookie that rotates on every use, so refreshes must not overlap across
// tabs either: the Web Lock makes the second tab wait and then use the cookie the first one set.
const LOCK_NAME = "fv-refresh";
const withRefreshLock = (task) => (navigator.locks?.request ? navigator.locks.request(LOCK_NAME, task) : task());

// The timeout keeps a hung request from holding the refresh lock (and every request waiting on it);
// it rejects without a response, so it counts as transient and the session is kept
const REFRESH_TIMEOUT_MS = 15000;
const postRefresh = () =>
    axios.post(`${API_BASE_URL}/user/refresh`, null, { withCredentials: true, timeout: REFRESH_TIMEOUT_MS });

// A 401 the server marks raced lost a rotation race with another tab, which holds a valid cookie
const isRaced = (error) => error?.response?.status === 401 && error.response.data?.raced === true;

// Refresh under the lock; a raced 401 is retried once (the lock now yields the other tab's new cookie)
const refreshUnderLock = () =>
    withRefreshLock(postRefresh).catch((error) => {
        if (!isRaced(error)) throw error;
        return withRefreshLock(postRefresh);
    });

// Exchange the refresh cookie for a new access token. Rejects with error.sessionExpired = true when
// the server says the session is over (401); any other failure (network, 429, 5xx) is transient and
// leaves the session as it was.
export const refreshAccessToken = () => {
    if (!refreshPromise) {
        refreshPromise = refreshUnderLock()
            .then((response) => {
                setToken(response.data.success);
                ensureSessionHint();
                return response.data.success;
            })
            .catch((error) => {
                if (error?.response?.status === 401) {
                    endSession();
                    error.sessionExpired = true;
                }
                throw error;
            })
            .finally(() => {
                refreshPromise = null;
            });
    }
    return refreshPromise;
};

// Current access token, refreshed first if it is about to expire. Null when signed out
// (ensureSession owns the refresh on page load).
export const getFreshToken = async () => {
    const token = getToken();
    if (!token || !isTokenExpiring(token)) return token;

    try {
        return await refreshAccessToken();
    } catch (error) {
        // Session over: nothing to send. Transient failure: the old token may still be valid
        return error?.sessionExpired ? null : token;
    }
};

let sessionCheck = null;

// Make sure this tab has a server-confirmed session, once per page load (and again after the token
// is lost). Resolves "authenticated" or "anonymous"; rejects when the server can't be reached.
export const ensureSession = () => {
    const token = getToken();
    if (token && !isTokenExpiring(token)) return Promise.resolve("authenticated");
    if (!token && !hasSessionHint()) return Promise.resolve("anonymous");

    if (!sessionCheck) {
        sessionCheck = refreshAccessToken()
            .then(
                () => "authenticated",
                (error) => {
                    if (error?.sessionExpired) return "anonymous";
                    // Keep going on the token we have; with none, report the outage
                    if (getToken()) return "authenticated";
                    throw error;
                }
            )
            .finally(() => {
                sessionCheck = null;
            });
    }
    return sessionCheck;
};

const redirectToLogin = () => {
    endSession();
    window.location.assign("/login");
};

// Pages that need a session; a tab on one is sent to the login page when the session ends
const PROTECTED_PATHS = ["/dashboard", "/shared-links", "/settings", "/admin"];

// Another tab signed in or out (it changed the session hint)
window.addEventListener("storage", (event) => {
    if (event.key !== null && event.key !== SESSION_HINT_KEY) return;

    if (!event.newValue) {
        clearToken();
        if (PROTECTED_PATHS.some((path) => window.location.pathname.startsWith(path))) {
            window.location.assign("/login");
        }
    } else if (event.newValue !== event.oldValue) {
        // A different login: don't keep showing the previous account's data
        window.location.reload();
    }
});

// Add auth header for all requests (login/register/refresh opt out with skipAuth)
api.interceptors.request.use(async (config) => {
    if (config.skipAuth) return config;

    const hadToken = Boolean(getToken());
    const token = await getFreshToken();
    if (hadToken && !token) {
        // The refresh found the session over: don't send the request unauthenticated (a 401 would
        // surface as an empty list or a generic error); go to the login page instead
        // Only protected pages are sent away; elsewhere (e.g. /confirm-email) the caller shows its own error
        if (PROTECTED_PATHS.some((path) => window.location.pathname.startsWith(path))) redirectToLogin();
        const error = new Error("Session expired");
        error.sessionExpired = true;
        throw error;
    }
    if (token) {
        config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
});

// Access token rejected: refresh once and retry, otherwise log out
api.interceptors.response.use(
    (response) => response,
    async (error) => {
        const config = error.config;
        if (error.response?.status !== 401 || !config || config.skipAuth || config._retried || !getToken()) {
            return Promise.reject(error);
        }

        config._retried = true;
        try {
            const token = await refreshAccessToken();
            config.headers.Authorization = `Bearer ${token}`;
            return api(config);
        } catch (refreshError) {
            // Only a rejected session logs out; a network error or rate limit must not
            if (refreshError?.sessionExpired) redirectToLogin();
            return Promise.reject(error);
        }
    }
);

// Log out locally straight away, then revoke the refresh token on the server
export const logout = () => {
    endSession();
    return api.post("/user/logout", null, { skipAuth: true }).catch(() => {});
};

// Best human-readable message for a failed request
export const getErrorMessage = (error, fallback = "Something went wrong") =>
    error?.response?.data?.error || error?.message || fallback;
