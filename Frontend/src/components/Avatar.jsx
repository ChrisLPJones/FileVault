import { useEffect, useRef, useState } from "react";
import { PROFILE_CHANGED_EVENT, getAvatarBlobAPI, getUserInfoAPI } from "../api/accountAPI";
import "./Avatar.css";

// The logged-in user's profile picture, or their initial when they haven't set one.
// Reloads whenever the profile changes (see notifyProfileChanged).
export default function Avatar({ size = 32 }) {
    const [profile, setProfile] = useState({ name: "", imageUrl: null });
    const urlRef = useRef(null);

    useEffect(() => {
        let cancelled = false;

        const load = async () => {
            try {
                const info = await getUserInfoAPI();
                const imageUrl = info.avatarUpdatedAt ? URL.createObjectURL(await getAvatarBlobAPI()) : null;
                if (cancelled) {
                    if (imageUrl) URL.revokeObjectURL(imageUrl);
                    return;
                }
                if (urlRef.current) URL.revokeObjectURL(urlRef.current);
                urlRef.current = imageUrl;
                setProfile({ name: `${info.firstName} ${info.lastName}`.trim(), imageUrl });
            } catch {
                // Keep whatever is showing (initial or previous picture)
            }
        };

        load();
        window.addEventListener(PROFILE_CHANGED_EVENT, load);
        return () => {
            cancelled = true;
            window.removeEventListener(PROFILE_CHANGED_EVENT, load);
            if (urlRef.current) URL.revokeObjectURL(urlRef.current);
            urlRef.current = null;
        };
    }, []);

    const style = { width: size, height: size, fontSize: Math.round(size * 0.45) };
    const initial = profile.name.charAt(0).toUpperCase() || "?";

    return profile.imageUrl ? (
        <img className="avatar" src={profile.imageUrl} alt={`${profile.name}'s profile picture`} style={style} />
    ) : (
        <span className="avatar avatar-initial" style={style} aria-hidden="true">
            {initial}
        </span>
    );
}
