import { api } from "../api/api";

// Don't throw on HTTP errors (the forms read the status and error message),
// and don't attach or refresh an access token for these calls
const authRequest = { validateStatus: () => true, skipAuth: true };

export const login = async (Email, Password) => {
    return api.post("/user/login", { Email, Password }, authRequest);
};

// Second login step when two-factor authentication is on: { challengeToken, code } or { challengeToken, recoveryCode }
export const loginTwoFactor = async (challengeToken, { code, recoveryCode }) => {
    return api.post("/user/login/2fa", { challengeToken, code, recoveryCode }, authRequest);
};

export const register =async (FirstName, LastName, Email, Password) => {
    return api.post("/user/register", { FirstName, LastName, Email, Password }, authRequest);
};
