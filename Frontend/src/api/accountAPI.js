import { api } from "./api";
import { clearToken, setToken } from "../utils/auth";

export const getUserInfoAPI = async () => (await api.get("/user/info")).data;

// { used, quota, maxUploadBytes } in bytes
export const getUsageAPI = async () => (await api.get("/user/usage")).data;

export const updateProfileAPI = async (username, email) => {
    const response = await api.patch("/user/profile", { username, email });
    setToken(response.data.token);
    return response.data;
};

export const changePasswordAPI = async (currentPassword, newPassword) => {
    const response = await api.post("/user/password", { currentPassword, newPassword });
    setToken(response.data.token);
    return response.data;
};

export const deleteAccountAPI = async () => {
    await api.delete("/user");
    clearToken();
};
