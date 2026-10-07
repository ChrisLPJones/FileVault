import { api } from "../api/api";

// Don't throw on HTTP errors (the forms read the status and error message),
// and don't attach or refresh an access token for these calls
const authRequest = { validateStatus: () => true, skipAuth: true };

export const login = async (Email, Password) => {
    return api.post("/user/login", { Email, Password }, authRequest);
};

export const register = async (Username, Email, Password) => {
    return api.post("/user/register", { Username, Email, Password }, authRequest);
};
