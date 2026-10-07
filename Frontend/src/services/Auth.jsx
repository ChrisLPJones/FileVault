import { api } from "../api/api";

// Don't throw on HTTP errors (the forms read the status and error message),
// and don't attach or refresh an access token for these calls
const authRequest = { validateStatus: () => true, skipAuth: true };

// identifier: email address or username
export const login = async (identifier, Password) => {
    return api.post("/user/login", { Login: identifier, Password }, authRequest);
};

export const register = async (Username, Email, Password) => {
    return api.post("/user/register", { Username, Email, Password }, authRequest);
};
