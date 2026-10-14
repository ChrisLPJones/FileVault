import { api } from "./api";

// Public site info (no sign-in needed): { privacy: null } on a self-hosted install, and in hosted
// mode { privacy: { inactiveDays, warningDays, trashRetentionDays, loginLocation, contactEmail? } }
export const getSiteInfoAPI = async () =>
    (await api.get("/site", { skipAuth: true, withCredentials: false })).data;
