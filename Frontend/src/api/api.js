import axios from "axios";
import { clearToken, getToken } from "../utils/auth";

export const API_BASE_URL = import.meta.env.VITE_API_BASE_URL;

export const api = axios.create({
    baseURL: API_BASE_URL,
});

// Add auth header for all requests
api.interceptors.request.use((config) => {
    const token = getToken();
    if (token) {
        config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
});

// Session expired or token rejected: log out and go to the login page
api.interceptors.response.use(
    (response) => response,
    (error) => {
        if (error.response?.status === 401 && getToken()) {
            clearToken();
            window.location.assign("/login");
        }
        return Promise.reject(error);
    }
);

// Best human-readable message for a failed request
export const getErrorMessage = (error, fallback = "Something went wrong") =>
    error?.response?.data?.error || error?.message || fallback;
