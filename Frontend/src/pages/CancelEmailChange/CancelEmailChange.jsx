import { useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { getErrorMessage } from "../../api/api";
import { cancelEmailChangeByLinkAPI } from "../../api/accountEmailAPI";
import "../Auth/Auth.css";

// Opened from the "email address change requested" notice sent to the old address
// (/cancel-email-change?token=...). Works without logging in. It signs out every device, so it
// waits for a click instead of acting on load (an email scanner or prefetch would fire it).
function CancelEmailChange() {
    const [params] = useSearchParams();
    const token = params.get("token") ?? "";
    const [submitting, setSubmitting] = useState(false);
    const [result, setResult] = useState(null);

    const handleCancel = async () => {
        setSubmitting(true);
        try {
            const data = await cancelEmailChangeByLinkAPI(token);
            setResult({ ok: true, message: data.success });
        } catch (err) {
            setResult({ ok: false, message: getErrorMessage(err, "Could not cancel the email change") });
        }
        setSubmitting(false);
    };

    return (
        <div className="auth-page">
            <div className="auth-card">
                <h1 className="auth-title">Cancel email change</h1>
                {!token && (
                    <div className="auth-alert auth-alert-top danger" role="alert">This link is incomplete. Open the link from the email again.</div>
                )}
                {token && !result && (
                    <>
                        <p className="auth-subtitle">
                            Someone asked to change the email address of your FileVault account. If that wasn&apos;t you,
                            cancel it here. This also signs you out of every device.
                        </p>
                        <button type="button" className="auth-button" disabled={submitting} onClick={handleCancel}>
                            {submitting ? "Cancelling…" : "Cancel the change and sign out everywhere"}
                        </button>
                    </>
                )}
                {result && (
                    <>
                        <div className={`auth-alert auth-alert-top ${result.ok ? "success" : "danger"}`} role={result.ok ? "status" : "alert"}>
                            {result.message}
                        </div>
                        {result.ok && (
                            <p className="auth-subtitle">
                                <Link to="/forgot-password">Reset your password</Link> to be safe, then log in again.
                            </p>
                        )}
                        <Link to="/login" className="auth-button auth-button-link">Log in</Link>
                    </>
                )}
            </div>
        </div>
    );
}

export default CancelEmailChange;
