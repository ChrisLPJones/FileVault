import { useEffect, useState } from "react";
import { getUserAvatarBlobAPI } from "../../api/adminAPI";
import "../../components/Avatar.css";

// Another user's profile picture for the admin list, or their initial when they have none (or
// it can't be loaded). The image is fetched as a blob through the authenticated API client and
// shown from an object URL, so the access token is never part of an image URL.
// `version` (the picture's last-changed time) reloads it when the picture changes.
export default function UserAvatar({ userId, name, version, size = 32 }) {
    const [loaded, setLoaded] = useState(null); // { key, url } of the image fetched for `key`
    const key = version ? `${userId}:${version}` : null;

    useEffect(() => {
        if (!key) return undefined;
        let cancelled = false;
        let url = null;
        getUserAvatarBlobAPI(userId)
            .then((blob) => {
                if (cancelled) return;
                url = URL.createObjectURL(blob);
                setLoaded({ key, url });
            })
            .catch(() => {
                // No picture after all: the initial stays
            });
        return () => {
            cancelled = true;
            if (url) URL.revokeObjectURL(url);
        };
    }, [key, userId]);

    const style = { width: size, height: size, fontSize: Math.round(size * 0.45) };
    if (key && loaded?.key === key) {
        return <img className="avatar" src={loaded.url} alt={`${name}'s profile picture`} style={style} />;
    }
    return (
        <span className="avatar avatar-initial" style={style} aria-hidden="true">
            {name.trim().charAt(0).toUpperCase() || "?"}
        </span>
    );
}
