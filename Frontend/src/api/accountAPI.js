import { api } from "./api";
import { endSession, setToken } from "../utils/auth";

// Fired after the profile or profile picture changes so every avatar reloads
export const PROFILE_CHANGED_EVENT = "fv-profile-change";
export const notifyProfileChanged = () => window.dispatchEvent(new Event(PROFILE_CHANGED_EVENT));

// { firstName, lastName, email, avatarUpdatedAt, emailVerified, hostedNotice, hostedInactiveDays, hostedContactEmail }
export const getUserInfoAPI = async () => (await api.get("/user/info")).data;

// Hosted mode: remember that this user closed the first-login notice
export const dismissHostedNoticeAPI = async () => (await api.post("/user/notices/hosted/dismiss")).data;

// { used, quota, maxUploadBytes, maxFileBytes } in bytes; used includes files in the recycle bin
export const getUsageAPI = async () => (await api.get("/user/usage")).data;

export const updateProfileAPI = async (firstName, lastName, email) => {
    const response = await api.patch("/user/profile", { firstName, lastName, email });
    setToken(response.data.token);
    notifyProfileChanged();
    return response.data;
};

export const changePasswordAPI = async (currentPassword, newPassword) => {
    const response = await api.post("/user/password", { currentPassword, newPassword });
    setToken(response.data.token);
    return response.data;
};

export const deleteAccountAPI = async () => {
    await api.delete("/user");
    endSession();
};

export const getAvatarBlobAPI = async () => (await api.get("/user/avatar", { responseType: "blob" })).data;

export const uploadAvatarAPI = async (imageBlob) => {
    const form = new FormData();
    form.append("avatar", imageBlob, imageBlob.type === "image/webp" ? "avatar.webp" : "avatar.png");
    await api.put("/user/avatar", form);
    notifyProfileChanged();
};

export const deleteAvatarAPI = async () => {
    await api.delete("/user/avatar");
    notifyProfileChanged();
};

// "default" | "windows" | "macos" | "ubuntu"; saved on the account
export const setIconThemeAPI = async (iconTheme) => (await api.put("/user/icon-theme", { iconTheme })).data;
