import { useUserProfile } from "../hooks/useUserProfile";
import "./Avatar.css";

// The logged-in user's profile picture, or their initial when they haven't set one.
// Updates whenever the profile changes (see notifyProfileChanged).
export default function Avatar({ size = 32 }) {
    const profile = useUserProfile();

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
