import { useEffect, useState } from "react";
import { getErrorMessage } from "../../api/api";
import { getSharesAPI, revokeShareAPI } from "../../api/shareAPI";
import FileTypeIcon from "../../components/FileTypeIcon/FileTypeIcon";
import "./SharedLinksCard.css";

const formatDay = (value) =>
    new Date(value).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });

const describe = (share, now) => {
    const parts = [`Created ${formatDay(share.createdAt)}`];
    if (!share.expiresAt) parts.push("never expires");
    else if (new Date(share.expiresAt) <= now) parts.push("expired");
    else parts.push(`expires ${new Date(share.expiresAt).toLocaleString()}`);
    if (share.hasPassword) parts.push("password");
    parts.push(`${share.downloadCount} ${share.downloadCount === 1 ? "download" : "downloads"}`);
    return parts.join(" · ");
};

// Settings card listing the user's share links, each with a Revoke button
export default function SharedLinksCard() {
    const [shares, setShares] = useState(null);
    const [status, setStatus] = useState(null);
    const [revoking, setRevoking] = useState(null);

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

    const now = new Date();

    return (
        <section className="settings-card" aria-labelledby="shares-heading">
            <h2 id="shares-heading">Shared links</h2>
            <p className="settings-hint">
                Anyone with one of these links can download the item. Revoke a link to stop it working.
            </p>

            {shares === null && !status && <p className="settings-hint">Loading…</p>}
            {shares?.length === 0 && (
                <p className="settings-empty">You haven't shared anything yet. Select a file or folder and choose Share.</p>
            )}

            {shares?.length > 0 && (
                <ul className="shared-links">
                    {shares.map((share) => {
                        const expired = share.expiresAt && new Date(share.expiresAt) <= now;
                        return (
                            <li key={share.id} className={expired ? "expired" : undefined}>
                                <FileTypeIcon name={share.name} isDirectory={share.isDirectory} size={28} />
                                <div className="shared-link-text">
                                    <span className="shared-link-name" title={share.name}>{share.name}</span>
                                    <span className="shared-link-meta">{describe(share, now)}</span>
                                </div>
                                <button
                                    type="button"
                                    className="settings-button secondary small"
                                    disabled={revoking === share.id}
                                    onClick={() => handleRevoke(share)}
                                    aria-label={`Revoke link to ${share.name}`}
                                >
                                    {revoking === share.id ? "Revoking…" : "Revoke"}
                                </button>
                            </li>
                        );
                    })}
                </ul>
            )}

            {status && (
                <div className={`settings-alert ${status.type}`} role={status.type === "danger" ? "alert" : "status"}>
                    {status.message}
                </div>
            )}
        </section>
    );
}
