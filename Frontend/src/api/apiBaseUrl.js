// The API's base URL: the runtime config (public/config.js, written by the Docker image from
// API_URL) wins, including an empty string (same origin); otherwise the build-time
// VITE_API_BASE_URL used by `npm run dev` and Vitest.
export function resolveApiBaseUrl(runtimeConfig, envUrl) {
    if (typeof runtimeConfig?.apiUrl === "string") return runtimeConfig.apiUrl;
    return envUrl ?? "";
}
