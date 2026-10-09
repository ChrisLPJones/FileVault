import { api } from "./api";
import { notifyProfileChanged } from "./accountAPI";

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
