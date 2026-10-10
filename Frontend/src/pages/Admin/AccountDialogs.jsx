import { useEffect, useId, useState } from "react";
import { getErrorMessage } from "../../api/api";
import { meetsPasswordRules } from "../../utils/passwordRules";

// A modal box over the page: closes with Escape, or Cancel. Content goes in children.
function Dialog({ title, onClose, children }) {
    const titleId = useId();

    useEffect(() => {
        const onKeyDown = (event) => {
            if (event.key === "Escape") onClose();
        };
        document.addEventListener("keydown", onKeyDown);
        return () => document.removeEventListener("keydown", onKeyDown);
    }, [onClose]);

    return (
        <div className="admin-dialog-backdrop">
            <div className="admin-dialog" role="dialog" aria-modal="true" aria-labelledby={titleId}>
                <h2 id={titleId}>{title}</h2>
                {children}
            </div>
        </div>
    );
}

// Run an async action for a dialog: tracks "working" and the server's error message
function useAction(action, fallbackMessage) {
    const [working, setWorking] = useState(false);
    const [error, setError] = useState(null);

    const run = async () => {
        setWorking(true);
        setError(null);
        try {
            await action();
        } catch (err) {
            setError(getErrorMessage(err, fallbackMessage));
            setWorking(false);
        }
    };

    return { working, error, run };
}

// Type a new password for someone else. They are signed out everywhere when it is saved.
export function SetPasswordDialog({ user, onSave, onClose }) {
    const [password, setPassword] = useState("");
    const { working, error, run } = useAction(() => onSave(user, password), "Could not set the password");
    const valid = meetsPasswordRules(password);

    return (
        <Dialog title={`Set password for ${user.email}`} onClose={onClose}>
            <form
                onSubmit={(event) => {
                    event.preventDefault();
                    if (valid && !working) run();
                }}
            >
                <p className="admin-muted">
                    They will be signed out everywhere straight away, and will need this password to sign in again.
                </p>
                <label className="admin-field">
                    <span>New password</span>
                    <input
                        type="password"
                        autoComplete="new-password"
                        value={password}
                        onChange={(event) => setPassword(event.target.value)}
                        autoFocus
                    />
                </label>
                <p className="admin-muted">At least 8 characters, with an uppercase letter, a lowercase letter and a number.</p>
                {error && <div className="admin-error" role="alert">{error}</div>}
                <div className="admin-dialog-actions">
                    <button type="submit" className="admin-button" disabled={!valid || working}>Set password</button>
                    <button type="button" className="admin-button secondary" disabled={working} onClick={onClose}>Cancel</button>
                </div>
            </form>
        </Dialog>
    );
}

// Delete someone's account and all their files. The button stays disabled until their email is typed.
export function DeleteAccountDialog({ user, onDelete, onClose }) {
    const [typed, setTyped] = useState("");
    const { working, error, run } = useAction(() => onDelete(user), "Could not delete the account");
    const matches = typed.trim().toLowerCase() === user.email.toLowerCase();

    return (
        <Dialog title="Delete account" onClose={onClose}>
            <form
                onSubmit={(event) => {
                    event.preventDefault();
                    if (matches && !working) run();
                }}
            >
                <p>
                    This permanently deletes the account for <strong>{user.email}</strong> and every file they stored.
                    It can't be undone.
                </p>
                <label className="admin-field">
                    <span>Type {user.email} to confirm</span>
                    <input
                        type="text"
                        autoComplete="off"
                        value={typed}
                        onChange={(event) => setTyped(event.target.value)}
                        autoFocus
                    />
                </label>
                {error && <div className="admin-error" role="alert">{error}</div>}
                <div className="admin-dialog-actions">
                    <button type="submit" className="admin-button danger" disabled={!matches || working}>Delete account</button>
                    <button type="button" className="admin-button secondary" disabled={working} onClick={onClose}>Cancel</button>
                </div>
            </form>
        </Dialog>
    );
}
