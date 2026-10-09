import { api } from "./api";

// Two-factor authentication (authenticator app)

// { enabled, recoveryCodesLeft }
export const getTwoFactorStatusAPI = async () => (await api.get("/user/2fa")).data;

// { secret, otpAuthUri } for the QR code
export const startTwoFactorSetupAPI = async (password) => (await api.post("/user/2fa/setup", { password })).data;

// Fired when sessions were signed out elsewhere on the page, so the sessions list reloads
export const SESSIONS_CHANGED_EVENT = "fv-sessions-change";
export const notifySessionsChanged = () => window.dispatchEvent(new Event(SESSIONS_CHANGED_EVENT));

// Turns 2FA on and signs out every other session.
// Returns { recoveryCodes (only ever shown once), otherSessionsSignedOut }
export const enableTwoFactorAPI = async (code) => {
    const data = (await api.post("/user/2fa/enable", { code })).data;
    notifySessionsChanged();
    return data;
};

// Needs the password and either a code or a recovery code
export const disableTwoFactorAPI = async (password, { code, recoveryCode }) =>
    (await api.post("/user/2fa/disable", { password, code, recoveryCode })).data;

export const regenerateRecoveryCodesAPI = async (code) =>
    (await api.post("/user/2fa/recovery-codes", { code })).data.recoveryCodes;

// Active sessions

// [{ id, device, ipAddress, createdAt, lastActiveAt, isCurrent }], this device first
export const getSessionsAPI = async () => (await api.get("/user/sessions")).data;

export const revokeSessionAPI = async (id) => (await api.delete(`/user/sessions/${encodeURIComponent(id)}`)).data;

export const revokeOtherSessionsAPI = async () => (await api.post("/user/sessions/revoke-others")).data;
