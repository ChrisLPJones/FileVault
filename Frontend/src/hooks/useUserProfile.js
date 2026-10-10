import { useSyncExternalStore } from "react";
import { PROFILE_CHANGED_EVENT, getAvatarBlobAPI, getUserInfoAPI } from "../api/accountAPI";
import { getAdminStatusAPI } from "../api/adminAPI";
import { setIconTheme } from "../utils/iconTheme";

// The logged-in user's name, email, picture and whether they're an admin, fetched once and shared by everything that
// shows them (avatars, the account menu, the folder tree footer). Reloaded when the profile
// changes (see notifyProfileChanged) and dropped once nothing shows it, e.g. after logging out,
// so the next account never sees the previous one's details.
// emailVerified starts true so the "confirm your email" banner only shows once the server says so
const EMPTY = { firstName: "", lastName: "", name: "", email: "", emailVerified: true, imageUrl: null, isAdmin: false,
    hostedNotice: false, hostedContactEmail: null, hostedInactiveDays: 30 };

let profile = EMPTY;
let loadId = 0;
const listeners = new Set();

const emit = () => listeners.forEach((listener) => listener());

const load = async () => {
    const id = ++loadId;
    try {
        const [info, admin] = await Promise.all([
            getUserInfoAPI(),
            // Only decides whether the Admin link shows; the admin pages check again on the server
            getAdminStatusAPI().catch(() => ({ isAdmin: false })),
        ]);
        const imageUrl = info.avatarUpdatedAt ? URL.createObjectURL(await getAvatarBlobAPI()) : null;
        if (id !== loadId) {
            // A newer load started (or everyone unsubscribed) while this one was running
            if (imageUrl) URL.revokeObjectURL(imageUrl);
            return;
        }
        if (profile.imageUrl) URL.revokeObjectURL(profile.imageUrl);
        profile = {
            firstName: info.firstName,
            lastName: info.lastName,
            name: `${info.firstName} ${info.lastName}`.trim(),
            email: info.email,
            emailVerified: info.emailVerified !== false,
            imageUrl,
            isAdmin: !!admin?.isAdmin,
            hostedNotice: info.hostedNotice === true,
            hostedContactEmail: info.hostedContactEmail || null,
            hostedInactiveDays: info.hostedInactiveDays || 30,
        };
        setIconTheme(info.iconTheme); // the account's icon style applies everywhere icons show
        emit();
    } catch {
        // Keep whatever is showing
    }
};

const subscribe = (listener) => {
    listeners.add(listener);
    if (listeners.size === 1) {
        window.addEventListener(PROFILE_CHANGED_EVENT, load);
        load();
    }

    return () => {
        listeners.delete(listener);
        if (listeners.size > 0) return;
        window.removeEventListener(PROFILE_CHANGED_EVENT, load);
        loadId++; // ignore a load still in flight
        if (profile.imageUrl) URL.revokeObjectURL(profile.imageUrl);
        profile = EMPTY;
        setIconTheme("default"); // don't carry this account's icons over to the next one
    };
};

const getSnapshot = () => profile;

export const useUserProfile = () => useSyncExternalStore(subscribe, getSnapshot);
