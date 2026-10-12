import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { getErrorMessage } from "../../api/api";
import {
    changePasswordAPI,
    deleteAccountAPI,
    deleteAvatarAPI,
    getUsageAPI,
    getUserInfoAPI,
    updateProfileAPI,
    uploadAvatarAPI,
} from "../../api/accountAPI";
import Avatar from "../../components/Avatar";
import { resizeImageToSquare } from "../../utils/avatarImage";
import { formatBytes } from "../../utils/formatBytes";
import { meetsPasswordRules, passwordRules } from "../../utils/passwordRules";
import { MdColorize } from "react-icons/md";
import { ACCENT_PRESETS, THEME_OPTIONS, setAccent, setThemePreference, useAccent, useTheme } from "../../utils/theme";
import TwoFactorCard from "./TwoFactorCard";
import SessionsCard from "./SessionsCard";
import EmailAddress from "./EmailAddress";
import "./Settings.css";

const DELETE_CONFIRMATION = "DELETE";
const THEME_LABELS = { system: "System", light: "Light", dark: "Dark" };

// Success/error message under a form
const Status = ({ status }) =>
    status ? (
        <div className={`settings-alert ${status.type}`} role={status.type === "danger" ? "alert" : "status"}>
            {status.message}
        </div>
    ) : null;

function Settings() {
    const navigate = useNavigate();
    const { preference } = useTheme();
    const accent = useAccent();

    // Esc goes back to the files (unless typing in a field)
    useEffect(() => {
        const onKeyDown = (event) => {
            if (event.key !== "Escape" || event.defaultPrevented) return;
            if (event.target.closest?.("input, textarea, select")) return;
            navigate("/dashboard");
        };
        document.addEventListener("keydown", onKeyDown);
        return () => document.removeEventListener("keydown", onKeyDown);
    }, [navigate]);
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState(null);
    const [usage, setUsage] = useState(null);

    const [profile, setProfile] = useState({ firstName: "", lastName: "" });
    const [profileStatus, setProfileStatus] = useState(null);
    const [savedEmail, setSavedEmail] = useState("");
    const [pendingEmail, setPendingEmail] = useState(null);
    const [hasAvatar, setHasAvatar] = useState(false);
    const [savingAvatar, setSavingAvatar] = useState(false);
    const [avatarStatus, setAvatarStatus] = useState(null);
    const [savingProfile, setSavingProfile] = useState(false);

    const [passwords, setPasswords] = useState({ current: "", next: "", confirm: "" });
    const [passwordStatus, setPasswordStatus] = useState(null);
    const [savingPassword, setSavingPassword] = useState(false);

    const [deleteConfirm, setDeleteConfirm] = useState("");
    const [deleteStatus, setDeleteStatus] = useState(null);
    const [deleting, setDeleting] = useState(false);

    useEffect(() => {
        let cancelled = false;

        Promise.all([getUserInfoAPI(), getUsageAPI()])
            .then(([info, usageData]) => {
                if (cancelled) return;
                setProfile({ firstName: info.firstName, lastName: info.lastName });
                setSavedEmail(info.email);
                setPendingEmail(info.pendingEmail ? { email: info.pendingEmail, expiresAt: info.pendingEmailExpiresAt } : null);
                setHasAvatar(!!info.avatarUpdatedAt);
                setUsage(usageData);
            })
            .catch((err) => !cancelled && setLoadError(getErrorMessage(err, "Could not load your account")))
            .finally(() => !cancelled && setLoading(false));

        return () => {
            cancelled = true;
        };
    }, []);

    const handleAvatarChange = async (event) => {
        const file = event.target.files?.[0];
        event.target.value = ""; // allow picking the same file again
        if (!file) return;

        setAvatarStatus(null);
        setSavingAvatar(true);
        try {
            await uploadAvatarAPI(await resizeImageToSquare(file));
            setHasAvatar(true);
            setAvatarStatus({ type: "success", message: "Profile picture updated" });
        } catch (err) {
            setAvatarStatus({ type: "danger", message: getErrorMessage(err, "Could not update profile picture") });
        } finally {
            setSavingAvatar(false);
        }
    };

    const handleAvatarRemove = async () => {
        setAvatarStatus(null);
        setSavingAvatar(true);
        try {
            await deleteAvatarAPI();
            setHasAvatar(false);
            setAvatarStatus({ type: "success", message: "Profile picture removed" });
        } catch (err) {
            setAvatarStatus({ type: "danger", message: getErrorMessage(err, "Could not remove profile picture") });
        } finally {
            setSavingAvatar(false);
        }
    };

    const handleProfileSubmit = async (event) => {
        event.preventDefault();
        setProfileStatus(null);
        setSavingProfile(true);
        try {
            await updateProfileAPI(profile.firstName.trim(), profile.lastName.trim());
            setProfileStatus({ type: "success", message: "Profile updated" });
        } catch (err) {
            setProfileStatus({ type: "danger", message: getErrorMessage(err, "Could not update profile") });
        } finally {
            setSavingProfile(false);
        }
    };

    const handlePasswordSubmit = async (event) => {
        event.preventDefault();
        setPasswordStatus(null);

        if (!meetsPasswordRules(passwords.next)) {
            setPasswordStatus({ type: "danger", message: "New password doesn't meet the requirements" });
            return;
        }
        if (passwords.next !== passwords.confirm) {
            setPasswordStatus({ type: "danger", message: "New passwords do not match" });
            return;
        }

        setSavingPassword(true);
        try {
            await changePasswordAPI(passwords.current, passwords.next);
            setPendingEmail(null); // the server drops a pending email change when the password changes
            setPasswords({ current: "", next: "", confirm: "" });
            setPasswordStatus({ type: "success", message: "Password changed. You've been signed out on other devices." });
        } catch (err) {
            setPasswordStatus({ type: "danger", message: getErrorMessage(err, "Could not change password") });
        } finally {
            setSavingPassword(false);
        }
    };

    const handleDelete = async (event) => {
        event.preventDefault();
        if (deleteConfirm !== DELETE_CONFIRMATION) return;

        setDeleteStatus(null);
        setDeleting(true);
        try {
            await deleteAccountAPI();
            navigate("/login", { replace: true });
        } catch (err) {
            setDeleteStatus({ type: "danger", message: getErrorMessage(err, "Could not delete account") });
            setDeleting(false);
        }
    };

    if (loading) {
        return <div className="settings-page"><p className="settings-loading">Loading…</p></div>;
    }

    if (loadError) {
        return (
            <div className="settings-page">
                <div className="settings-card">
                    <Status status={{ type: "danger", message: loadError }} />
                </div>
            </div>
        );
    }

    const usedPercent = usage.quota > 0 ? Math.min(100, (usage.used / usage.quota) * 100) : 100;
    const usageLevel = usedPercent >= 90 ? "high" : usedPercent >= 75 ? "medium" : "low";

    return (
        <div className="settings-page">
            <h1 className="settings-title">Account settings</h1>

            <section className="settings-card" aria-labelledby="storage-heading">
                <h2 id="storage-heading">Storage</h2>
                <div
                    className="usage-bar"
                    role="progressbar"
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={Math.round(usedPercent)}
                    aria-label="Storage used"
                >
                    <div className={`usage-bar-fill ${usageLevel}`} style={{ width: `${usedPercent}%` }} />
                </div>
                <p className="usage-text">
                    {formatBytes(usage.used)} of {formatBytes(usage.quota)} used
                    {" · "}
                    {formatBytes(usage.quota - usage.used)} free
                </p>
                <p className="settings-hint">Files can be up to {formatBytes(usage.maxFileBytes ?? usage.maxUploadBytes, 0)} each.</p>
            </section>

            <section className="settings-card" aria-labelledby="appearance-heading">
                <h2 id="appearance-heading">Appearance</h2>
                <div className="theme-options" role="radiogroup" aria-labelledby="appearance-heading">
                    {THEME_OPTIONS.map((option) => (
                        <label key={option} className={`theme-option ${preference === option ? "selected" : ""}`}>
                            <input
                                type="radio"
                                name="theme"
                                value={option}
                                checked={preference === option}
                                onChange={() => setThemePreference(option)}
                            />
                            {THEME_LABELS[option]}
                        </label>
                    ))}
                </div>
                <p className="settings-hint">System follows your device's light or dark setting. Saved on this device.</p>

                <h3 id="accent-heading" className="accent-heading">Accent colour</h3>
                <div className="accent-options" role="group" aria-labelledby="accent-heading">
                    {ACCENT_PRESETS.map((color) => (
                        <button
                            key={color}
                            type="button"
                            className={`accent-swatch ${accent === color ? "selected" : ""}`}
                            style={{ backgroundColor: color }}
                            aria-label={color}
                            aria-pressed={accent === color}
                            onClick={() => setAccent(color)}
                        />
                    ))}
                    <label
                        className={`accent-custom ${accent && !ACCENT_PRESETS.includes(accent) ? "selected" : ""}`}
                        style={accent && !ACCENT_PRESETS.includes(accent) ? { backgroundColor: accent } : undefined}
                        title="Pick a custom colour"
                    >
                        <MdColorize size={16} aria-hidden="true" />
                        <input
                            type="color"
                            aria-label="Custom accent colour"
                            value={accent || "#007bff"}
                            onChange={(e) => setAccent(e.target.value)}
                        />
                    </label>
                </div>
            </section>

            <section className="settings-card" aria-labelledby="profile-heading">
                <h2 id="profile-heading">Profile</h2>

                <div className="avatar-editor">
                    <Avatar size={72} />
                    <div className="avatar-editor-actions">
                        <label className={`settings-button secondary ${savingAvatar ? "disabled" : ""}`}>
                            {savingAvatar ? "Saving…" : hasAvatar ? "Change picture" : "Upload picture"}
                            <input
                                type="file"
                                accept="image/png,image/jpeg,image/webp"
                                className="visually-hidden"
                                disabled={savingAvatar}
                                onChange={handleAvatarChange}
                            />
                        </label>
                        {hasAvatar && (
                            <button type="button" className="settings-button secondary" disabled={savingAvatar} onClick={handleAvatarRemove}>
                                Remove
                            </button>
                        )}
                        <p className="settings-hint">PNG, JPEG or WebP. It's cropped to a square and resized to 256×256.</p>
                    </div>
                </div>
                <Status status={avatarStatus} />

                <form onSubmit={handleProfileSubmit}>
                    <div className="form-group">
                        <label htmlFor="settings-first-name">First name</label>
                        <input
                            id="settings-first-name"
                            type="text"
                            value={profile.firstName}
                            maxLength={50}
                            required
                            onChange={(e) => setProfile({ ...profile, firstName: e.target.value })}
                        />
                    </div>
                    <div className="form-group">
                        <label htmlFor="settings-last-name">Last name <span className="settings-optional">(optional)</span></label>
                        <input
                            id="settings-last-name"
                            type="text"
                            value={profile.lastName}
                            maxLength={50}
                            onChange={(e) => setProfile({ ...profile, lastName: e.target.value })}
                        />
                    </div>
                    <button type="submit" className="settings-button" disabled={savingProfile}>
                        {savingProfile ? "Saving…" : "Save profile"}
                    </button>
                    <Status status={profileStatus} />
                </form>

                <EmailAddress key={pendingEmail?.email ?? "none"} email={savedEmail} pending={pendingEmail} />
            </section>

            <section className="settings-card" aria-labelledby="password-heading">
                <h2 id="password-heading">Change password</h2>
                <form onSubmit={handlePasswordSubmit}>
                    <input type="text" name="username" autoComplete="username" value={savedEmail} readOnly hidden />
                    <div className="form-group">
                        <label htmlFor="current-password">Current password</label>
                        <input
                            id="current-password"
                            type="password"
                            autoComplete="current-password"
                            value={passwords.current}
                            required
                            onChange={(e) => setPasswords({ ...passwords, current: e.target.value })}
                        />
                    </div>
                    <div className="form-group">
                        <label htmlFor="new-password">New password</label>
                        <input
                            id="new-password"
                            type="password"
                            autoComplete="new-password"
                            value={passwords.next}
                            required
                            onChange={(e) => setPasswords({ ...passwords, next: e.target.value })}
                        />
                        <ul className="password-rules">
                            {passwordRules.map((rule) => (
                                <li key={rule.label} className={!passwords.next ? undefined : rule.test(passwords.next) ? "met" : "unmet"}>
                                    {rule.label}
                                </li>
                            ))}
                        </ul>
                    </div>
                    <div className="form-group">
                        <label htmlFor="confirm-password">Confirm new password</label>
                        <input
                            id="confirm-password"
                            type="password"
                            autoComplete="new-password"
                            value={passwords.confirm}
                            required
                            onChange={(e) => setPasswords({ ...passwords, confirm: e.target.value })}
                        />
                    </div>
                    <button type="submit" className="settings-button" disabled={savingPassword}>
                        {savingPassword ? "Changing…" : "Change password"}
                    </button>
                    <Status status={passwordStatus} />
                </form>
            </section>

            <section className="settings-card" aria-labelledby="shares-heading">
                <h2 id="shares-heading">Shared links</h2>
                <p className="settings-hint">See the links you've shared, copy them, show their passwords or revoke them.</p>
                <Link to="/shared-links" className="settings-button secondary settings-link-button">
                    Manage shared links
                </Link>
            </section>
            <TwoFactorCard email={savedEmail} />

            <SessionsCard />

            <section className="settings-card danger-zone" aria-labelledby="delete-heading">
                <h2 id="delete-heading">Delete account</h2>
                <p className="settings-hint">
                    This permanently deletes your account and every file you've stored. It can't be undone.
                </p>
                <form onSubmit={handleDelete}>
                    <div className="form-group">
                        <label htmlFor="delete-confirm">
                            Type <strong>{DELETE_CONFIRMATION}</strong> to confirm
                        </label>
                        <input
                            id="delete-confirm"
                            type="text"
                            autoComplete="off"
                            value={deleteConfirm}
                            onChange={(e) => setDeleteConfirm(e.target.value)}
                        />
                    </div>
                    <button
                        type="submit"
                        className="settings-button danger"
                        disabled={deleting || deleteConfirm !== DELETE_CONFIRMATION}
                    >
                        {deleting ? "Deleting…" : "Delete my account"}
                    </button>
                    <Status status={deleteStatus} />
                </form>
            </section>
        </div>
    );
}

export default Settings;
