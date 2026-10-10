import { useState } from "react";
import PropTypes from "prop-types";
import { getErrorMessage } from "../../api/api";
import { cancelEmailChangeAPI, requestEmailChangeAPI, resendEmailChangeAPI } from "../../api/accountEmailAPI";
import SettingsStatus from "./SettingsStatus";

// The account's email address and, while a change is waiting, the pending one. A new address
// only replaces the current one once its emailed link is confirmed (see the /confirm-email page);
// until then the current address stays the one to log in with.
function EmailAddress({ email, pending: initialPending }) {
    const [pending, setPending] = useState(initialPending);
    const [editing, setEditing] = useState(false);
    const [newEmail, setNewEmail] = useState("");
    const [password, setPassword] = useState("");
    const [busy, setBusy] = useState(false);
    const [status, setStatus] = useState(null);

    const run = async (action, failure) => {
        setStatus(null);
        setBusy(true);
        try {
            await action();
        } catch (err) {
            setStatus({ type: "danger", message: getErrorMessage(err, failure) });
        } finally {
            setBusy(false);
        }
    };

    const handleSubmit = (event) => {
        event.preventDefault();
        return run(async () => {
            const data = await requestEmailChangeAPI(newEmail.trim(), password);
            setPending({ email: data.pendingEmail, expiresAt: data.expiresAt });
            setStatus({ type: "success", message: data.success });
            setEditing(false);
            setNewEmail("");
            setPassword("");
        }, "Could not change the email address");
    };

    const handleResend = () =>
        run(async () => {
            const data = await resendEmailChangeAPI();
            setPending({ email: data.pendingEmail, expiresAt: data.expiresAt });
            setStatus({ type: "success", message: data.success });
        }, "Could not send the link");

    const handleCancel = () =>
        run(async () => {
            await cancelEmailChangeAPI();
            setPending(null);
            setStatus({ type: "success", message: "Email change cancelled" });
        }, "Could not cancel the change");

    return (
        <div className="email-address" aria-labelledby="email-address-heading" role="group">
            <h3 id="email-address-heading" className="email-address-heading">Email address</h3>
            <p className="email-address-current">
                <strong>{email}</strong>
                {!editing && (
                    <button type="button" className="settings-button secondary" disabled={busy} onClick={() => setEditing(true)}>
                        Change
                    </button>
                )}
            </p>

            {pending && (
                <div className="email-address-pending">
                    <p className="settings-hint">Waiting for you to confirm <strong>{pending.email}</strong> — check your inbox.</p>
                    <div className="email-address-actions">
                        <button type="button" className="settings-button secondary" disabled={busy} onClick={handleResend}>Resend link</button>
                        <button type="button" className="settings-button secondary" disabled={busy} onClick={handleCancel}>Cancel change</button>
                    </div>
                </div>
            )}

            {editing && (
                <form onSubmit={handleSubmit}>
                    <input type="text" name="username" autoComplete="username" value={email} readOnly hidden />
                    <div className="form-group">
                        <label htmlFor="settings-new-email">New email address</label>
                        <input
                            id="settings-new-email"
                            type="email"
                            autoComplete="email"
                            value={newEmail}
                            maxLength={100}
                            required
                            onChange={(e) => setNewEmail(e.target.value)}
                        />
                    </div>
                    <div className="form-group">
                        <label htmlFor="settings-email-password">Current password</label>
                        <input
                            id="settings-email-password"
                            type="password"
                            autoComplete="current-password"
                            value={password}
                            required
                            onChange={(e) => setPassword(e.target.value)}
                        />
                    </div>
                    <p className="settings-hint">We'll email a link to the new address. Your email stays as it is until you open it.</p>
                    <div className="email-address-actions">
                        <button type="submit" className="settings-button" disabled={busy}>
                            {busy ? "Sending…" : "Send confirmation link"}
                        </button>
                        <button
                            type="button"
                            className="settings-button secondary"
                            disabled={busy}
                            onClick={() => { setEditing(false); setNewEmail(""); setPassword(""); setStatus(null); }}
                        >
                            Close
                        </button>
                    </div>
                </form>
            )}
            <SettingsStatus status={status} />
        </div>
    );
}

EmailAddress.propTypes = {
    email: PropTypes.string.isRequired,
    pending: PropTypes.shape({
        email: PropTypes.string.isRequired,
        expiresAt: PropTypes.string.isRequired,
    }),
};

export default EmailAddress;
