import { useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { getErrorMessage } from "../../api/api";
import { getPublicShareAPI, shareDownloadUrl, unlockShareAPI } from "../../api/shareAPI";
import FileTypeIcon from "../../components/FileTypeIcon/FileTypeIcon";
import { formatBytes } from "../../utils/formatBytes";
import "../Auth/Auth.css";
import "./SharePage.css";

const MAX_SHOWN_ENTRIES = 200;

// The page behind a share link (/s/:token). Works without logging in.
function SharePage() {
    const { token } = useParams();
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState(null);
    const [details, setDetails] = useState(null);
    const [password, setPassword] = useState("");
    const [unlocking, setUnlocking] = useState(false);
    const [unlockError, setUnlockError] = useState(null);
    const [downloadStarted, setDownloadStarted] = useState(false);
    const formRef = useRef(null);

    useEffect(() => {
        let cancelled = false;
        getPublicShareAPI(token)
            .then((info) => !cancelled && setDetails(info))
            .catch((err) => {
                if (cancelled) return;
                setLoadError(err.response?.status === 404
                    ? "This link doesn't exist or has expired."
                    : getErrorMessage(err, "Could not open this link"));
            })
            .finally(() => !cancelled && setLoading(false));
        return () => {
            cancelled = true;
        };
    }, [token]);

    const locked = details?.passwordRequired && !details?.name;

    const handleUnlock = async (event) => {
        event.preventDefault();
        if (!password) {
            setUnlockError("Enter the password");
            return;
        }
        setUnlocking(true);
        setUnlockError(null);
        try {
            setDetails(await unlockShareAPI(token, password));
        } catch (err) {
            setUnlockError(err.response?.status === 401 ? "That password isn't right" : getErrorMessage(err, "Could not open this link"));
        } finally {
            setUnlocking(false);
        }
    };

    // A plain form post into a hidden frame: the browser streams the file straight to disk
    const handleDownload = () => {
        formRef.current?.submit();
        setDownloadStarted(true);
    };

    const renderBody = () => {
        if (loading) return <p className="auth-subtitle">Loading…</p>;

        if (loadError) {
            return (
                <>
                    <h1 className="auth-title">Link unavailable</h1>
                    <div className="auth-alert auth-alert-top danger" role="alert">{loadError}</div>
                    <p className="auth-switch">
                        Ask the person who shared it for a new link, or <Link to="/login">log in to FileVault</Link>.
                    </p>
                </>
            );
        }

        if (locked) {
            return (
                <>
                    <h1 className="auth-title">Protected link</h1>
                    <p className="auth-subtitle">Enter the password you were given to see what's shared.</p>
                    <form onSubmit={handleUnlock} noValidate>
                        <div className="auth-field">
                            <label htmlFor="share-password">Password</label>
                            <input
                                id="share-password"
                                type="password"
                                autoComplete="off"
                                value={password}
                                onChange={(e) => {
                                    setPassword(e.target.value);
                                    setUnlockError(null);
                                }}
                                aria-invalid={!!unlockError}
                                aria-describedby={unlockError ? "share-password-error" : undefined}
                            />
                            {unlockError && <div id="share-password-error" className="auth-field-error">{unlockError}</div>}
                        </div>
                        <button type="submit" className="auth-button" disabled={unlocking}>
                            {unlocking ? "Checking…" : "Continue"}
                        </button>
                    </form>
                </>
            );
        }

        const entries = details.files ?? [];
        const fileCount = entries.filter((entry) => !entry.isDirectory).length;

        return (
            <>
                <div className="share-item">
                    <FileTypeIcon name={details.name} isDirectory={details.isDirectory} size={56} />
                    <div className="share-item-text">
                        <h1 className="share-name" title={details.name}>{details.name}</h1>
                        <p className="share-meta">
                            {details.isDirectory
                                ? `Folder · ${fileCount} ${fileCount === 1 ? "file" : "files"} · ${formatBytes(details.size)}`
                                : formatBytes(details.size)}
                        </p>
                    </div>
                </div>

                {details.isDirectory && entries.length > 0 && (
                    <ul className="share-entries" aria-label="Folder contents">
                        {entries.slice(0, MAX_SHOWN_ENTRIES).map((entry) => (
                            <li key={entry.path}>
                                <FileTypeIcon name={entry.path} isDirectory={entry.isDirectory} size={18} />
                                <span className="share-entry-path" title={entry.path}>{entry.path}</span>
                                {!entry.isDirectory && <span className="share-entry-size">{formatBytes(entry.size)}</span>}
                            </li>
                        ))}
                        {entries.length > MAX_SHOWN_ENTRIES && (
                            <li className="share-entries-more">and {entries.length - MAX_SHOWN_ENTRIES} more…</li>
                        )}
                    </ul>
                )}

                <form ref={formRef} method="post" action={shareDownloadUrl(token)} target="share-download-frame">
                    <input type="hidden" name="password" value={password} />
                </form>
                <iframe name="share-download-frame" title="Download" className="share-download-frame" />

                <button type="button" className="auth-button" onClick={handleDownload}>
                    {details.isDirectory ? "Download as zip" : "Download"}
                </button>

                {downloadStarted && (
                    <div className="auth-alert success" role="status">Your download should start in a moment.</div>
                )}

                {details.expiresAt && (
                    <p className="share-expiry">This link expires {new Date(details.expiresAt).toLocaleString()}.</p>
                )}
            </>
        );
    };

    return (
        <div className="auth-page">
            <div className="auth-card share-card">{renderBody()}</div>
        </div>
    );
}

export default SharePage;
