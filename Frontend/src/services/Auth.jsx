import { api } from "../api/api";

// Don't throw on HTTP errors; the forms read the status and error message
const acceptAllStatuses = { validateStatus: () => true };

export const login = async (Email, Password) => {
    return api.post("/user/login", { Email, Password }, acceptAllStatuses);
};

export const register = async (Username, Email, Password) => {
    return api.post("/user/register", { Username, Email, Password }, acceptAllStatuses);
};
