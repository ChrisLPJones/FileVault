import { api } from "./api";

// { abuseContactEmail: string | null }. Public: no access token is sent or refreshed.
export const getSiteInfoAPI = async () => (await api.get("/site", { skipAuth: true })).data;
