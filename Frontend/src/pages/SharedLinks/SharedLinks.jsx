import { useEffect, useRef, useState } from "react";
import { FiCopy, FiEye, FiEyeOff } from "react-icons/fi";
import { getErrorMessage } from "../../api/api";
import { getSharePasswordAPI, getSharesAPI, revokeShareAPI, shareUrl } from "../../api/shareAPI";
import FileTypeIcon from "../../components/FileTypeIcon/FileTypeIcon";
import "../Settings/Settings.css";
import "./SharedLinks.css";

const formatDay = (value) =>
    new Date(value).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });

const describe = (share, now) => {
    const parts = [`Created ${formatDay(share.createdAt)}`];
    if (!share.expiresAt) parts.push("never expires");
    else if (new Date(share.expiresAt) <= now) parts.push("expired");
    else parts.push(`expires ${new Date(share.expiresAt).toLocaleString()}`);
    parts.push(`${share.downloadCount} ${share.downloadCount === 1 ? "download" : "downloads"}`);
    if (share.itemInBin) parts.push("not working while the item is in the recycle bin");
    return parts.join(" · ");
};

// One link: copy it, show or hide its password, revoke it
function SharedLinkRow({ share, now, onRevoke, revoking }) {
    const [copied, setCopied] = useState(false);
    const [password, setPassword] = useState(null); // fetched the first time it's shown
    const [showPassword, setShowPassword] = useState(false);
    const [passwordError, setPasswordError] = useState(null);
    const [loadingPassword, setLoadingPassword] = useState(false);
    const linkRef = useRef(null);
    const link = share.token ? shareUrl(share.token) : null;
    const expired = share.expiresAt && new Date(share.expiresAt) <= now;

    const handleCopy = async () => {
        try {
            await navigator.clipboard.writeText(link);
            setCopied(true);
            setTimeout(() => setCopied(false), 2000);
        } catch {
            linkRef.current?.select(); // clipboard blocked: select it to copy by hand
        }
    };

    const togglePassword = async () => {
        if (showPassword) {
            setShowPassword(false);
            return;
        }
        if (password === null) {
            setLoadingPassword(true);
            setPasswordError(null);
            try {
                setPassword(await getSharePasswordAPI(share.id));
            } catch (err) {
                setPasswordError(getErrorMessage(err, "Could not show the password"));
                return;
            } finally {
                setLoadingPassword(false);
            }
        }
        setShowPassword(true);
    };

    return (
        <li className={expired ? "expired" : undefined}>
            <div className="shared-link-head">
                <FileTypeIcon name={share.name} isDirectory={share.isDirectory} size={28} />
                <div className="shared-link-text">
                    <span className="shared-link-name" title={share.name}>{share.name}</span>
                    <span className="shared-link-meta">{describe(share, now)}</span>
                </div>
                <button
                    type="button"
                    className="settings-button secondary small"
                    disabled={revoking}
                    onClick={() => onRevoke(share)}
                    aria-label={`Revoke link to ${share.name}`}
                >
                    {revoking ? "Revoking…" : "Revoke"}
                </button>
            </div>

            <div className="shared-link-fields">
                {link ? (
                    <div className="shared-link-field">
                        <label htmlFor={`link-${share.id}`}>Link</label>
                        <div className="shared-link-input">
                            <input id={`link-${share.id}`} ref={linkRef} type="text" readOnly value={link} onFocus={(e) => e.target.select()} />
                            <button type="button" className="shared-link-icon-button" onClick={handleCopy} title="Copy link" aria-label={`Copy link to ${share.name}`}>
                                <FiCopy aria-hidden="true" />
                                <span>{copied ? "Copied" : "Copy"}</span>
                            </button>
                        </div>
                    </div>
                ) : (
                    <p className="shared-link-note">
                        Created before links could be shown again, so this link can't be copied. Revoke it and share again for a new one.
                    </p>
                )}

                {share.hasPassword && (
                    share.passwordViewable ? (
                        <div className="shared-link-field">
                            <label htmlFor={`password-${share.id}`}>Password</label>
                            <div className="shared-link-input">
                                <input
                                    id={`password-${share.id}`}
                                    type={showPassword ? "text" : "password"}
                                    readOnly
                                    value={showPassword ? password : "••••••••"}
                                    aria-describedby={passwordError ? `password-error-${share.id}` : undefined}
                                />
                                <button
                                    type="button"
                                    className="shared-link-icon-button"
                                    onClick={togglePassword}
                                    disabled={loadingPassword}
                                    aria-pressed={showPassword}
                                    title={showPassword ? "Hide password" : "Show password"}
                                    aria-label={showPassword ? "Hide password" : "Show password"}
                                >
                                    {showPassword ? <FiEyeOff aria-hidden="true" /> : <FiEye aria-hidden="true" />}
                                </button>
                            </div>
                            {passwordError && <div id={`password-error-${share.id}`} className="shared-link-error">{passwordError}</div>}
                        </div>
                    ) : (
                        <p className="shared-link-note">Password protected. The password can't be shown for links created before passwords were kept.</p>
                    )
                )}
            </div>
        </li>
    );
}

// Every link the user has shared, to copy, check the password of, or revoke
function SharedLinks() {
    const [shares, setShares] = useState(null);
    const [status, setStatus] = useState(null);
    const [revoking, setRevoking] = useState(null);
    const [now] = useState(() => new Date());

    useEffect(() => {
        let cancelled = false;
        getSharesAPI()
            .then((data) => !cancelled && setShares(data))
            .catch((err) => !cancelled && setStatus({ type: "danger", message: getErrorMessage(err, "Could not load your links") }));
        return () => {
            cancelled = true;
        };
    }, []);

    const handleRevoke = async (share) => {
        setRevoking(share.id);
        setStatus(null);
        try {
            await revokeShareAPI(share.id);
            setShares((prev) => prev.filter((s) => s.id !== share.id));
            setStatus({ type: "success", message: `Link to "${share.name}" revoked` });
        } catch (err) {
            setStatus({ type: "danger", message: getErrorMessage(err, "Could not revoke the link") });
        } finally {
            setRevoking(null);
        }
    };

    return (
        <div className="settings-page">
            <h1 className="settings-title">Shared links</h1>
            <section className="settings-card shared-links-card" aria-label="Your shared links">
                <p className="settings-hint">
                    Anyone with one of these links can download the item. Links with a password also need the password. Revoke a link to stop it working.
                </p>

                {status && (
                    <div className={`settings-alert ${status.type}`} role={status.type === "danger" ? "alert" : "status"}>
                        {status.message}
                    </div>
                )}

                {shares === null && !status && <p className="settings-hint">Loading…</p>}
                {shares?.length === 0 && (
                    <p className="settings-empty">You haven't shared anything yet. Select a file or folder and choose Share.</p>
                )}

                {shares?.length > 0 && (
                    <ul className="shared-links">
                        {shares.map((share) => (
                            <SharedLinkRow
                                key={share.id}
                                share={share}
                                now={now}
                                onRevoke={handleRevoke}
                                revoking={revoking === share.id}
                            />
                        ))}
                    </ul>
                )}
            </section>
        </div>
    );
}

export default SharedLinks;
