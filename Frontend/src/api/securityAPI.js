import { api } from "./api";

// Two-factor authentication (authenticator app)

// { enabled, recoveryCodesLeft }
export const getTwoFactorStatusAPI = async () => (await api.get("/user/2fa")).data;

// { secret, otpAuthUri } for the QR code
export const startTwoFactorSetupAPI = async (password) => (await api.post("/user/2fa/setup", { password })).data;

// Turns 2FA on; returns the recovery codes (only ever shown once)
export const enableTwoFactorAPI = async (code) => (await api.post("/user/2fa/enable", { code })).data.recoveryCodes;

// Needs the password and either a code or a recovery code
export const disableTwoFactorAPI = async (password, { code, recoveryCode }) =>
    (await api.post("/user/2fa/disable", { password, code, recoveryCode })).data;

export const regenerateRecoveryCodesAPI = async (code) =>
    (await api.post("/user/2fa/recovery-codes", { code })).data.recoveryCodes;
