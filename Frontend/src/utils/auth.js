import { jwtDecode } from "jwt-decode";

const TOKEN_KEY = "token";

export const getToken = () => localStorage.getItem(TOKEN_KEY);

export const setToken = (token) => localStorage.setItem(TOKEN_KEY, token);

export const clearToken = () => localStorage.removeItem(TOKEN_KEY);

// True if the access token expires within the next 30 seconds (or can't be read)
export const isTokenExpiring = (token) => {
    try {
        const { exp } = jwtDecode(token);
        return !exp || Date.now() >= exp * 1000 - 30_000;
    } catch {
        return true;
    }
};

// True if the user has a session. An expired access token still counts: the API
// client renews it with the refresh cookie, and logs out if that fails.
export const isAuthenticated = () => {
    const token = getToken();
    if (!token) return false;

    try {
        jwtDecode(token);
        return true;
    } catch {
        clearToken();
        return false;
    }
};
