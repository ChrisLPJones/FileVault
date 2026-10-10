import { api } from "./api";
import { notifyProfileChanged } from "./accountAPI";
import { setToken } from "../utils/auth";

// Forgot password and email verification. The links in the emails open the
// /reset-password and /verify-email pages, which work without being logged in.
const publicRequest = { skipAuth: true };

// Always succeeds with the same message, whether or not the address has an account
export const forgotPasswordAPI = async (email) =>
    (await api.post("/user/forgot-password", { email }, publicRequest)).data;

export const resetPasswordAPI = async (token, newPassword) =>
    (await api.post("/user/reset-password", { token, newPassword }, publicRequest)).data;

export const verifyEmailAPI = async (token) => {
    const data = (await api.post("/user/verify-email", { token }, publicRequest)).data;
    notifyProfileChanged(); // hides the "confirm your email" banner in other places
    return data;
};

export const resendVerificationAPI = async () => (await api.post("/user/resend-verification")).data;

// From the login page (not logged in): always succeeds with the same message
export const resendVerificationByEmailAPI = async (email) =>
    (await api.post("/user/resend-verification-email", { email }, publicRequest)).data;

// Changing the email address. The new address stays pending until the link emailed to it is
// confirmed while signed in as the same account; the current address is the login until then.
// -> { success, pendingEmail, expiresAt }
export const requestEmailChangeAPI = async (email, currentPassword) =>
    (await api.post("/user/email/change", { email, currentPassword })).data;

export const resendEmailChangeAPI = async () => (await api.post("/user/email/resend")).data;

export const cancelEmailChangeAPI = async () => (await api.delete("/user/email/pending")).data;

// -> { success, email, token }; the new access token carries the new email
export const confirmEmailChangeAPI = async (token) => {
    const data = (await api.post("/user/email/confirm", { token })).data;
    setToken(data.token);
    notifyProfileChanged();
    return data;
};

// The "this wasn't me" link in the notice sent to the old address: needs no login, and signs out every device
export const cancelEmailChangeByLinkAPI = async (token) =>
    (await api.post("/user/email/cancel", { token }, publicRequest)).data;
