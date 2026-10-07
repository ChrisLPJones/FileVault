import axios from "axios";
import { clearToken, getToken, isTokenExpiring, setToken } from "../utils/auth";

export const API_BASE_URL = import.meta.env.VITE_API_BASE_URL;

export const api = axios.create({
    baseURL: API_BASE_URL,
    // Send the httpOnly refresh-token cookie to /user/refresh and /user/logout
    withCredentials: true,
});

// One refresh at a time, shared by every request that needs it
let refreshPromise = null;

// Exchange the refresh cookie for a new access token
export const refreshAccessToken = () => {
    if (!refreshPromise) {
        refreshPromise = axios
            .post(`${API_BASE_URL}/user/refresh`, null, { withCredentials: true })
            .then((response) => {
                setToken(response.data.success);
                return response.data.success;
            })
            .finally(() => {
                refreshPromise = null;
            });
    }
    return refreshPromise;
};

// Current access token, refreshed first if it is about to expire
export const getFreshToken = async () => {
    const token = getToken();
    if (!token || !isTokenExpiring(token)) return token;

    try {
        return await refreshAccessToken();
    } catch {
        // Another tab may have refreshed already
        const latest = getToken();
        return latest && !isTokenExpiring(latest) ? latest : token;
    }
};

const redirectToLogin = () => {
    clearToken();
    window.location.assign("/login");
};

// Add auth header for all requests (login/register/refresh opt out with skipAuth)
api.interceptors.request.use(async (config) => {
    if (config.skipAuth) return config;

    const token = await getFreshToken();
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
        } catch {
            redirectToLogin();
            return Promise.reject(error);
        }
    }
);

// Log out locally straight away, then revoke the refresh token on the server
export const logout = () => {
    clearToken();
    return api.post("/user/logout", null, { skipAuth: true }).catch(() => {});
};

// Best human-readable message for a failed request
export const getErrorMessage = (error, fallback = "Something went wrong") =>
    error?.response?.data?.error || error?.message || fallback;
