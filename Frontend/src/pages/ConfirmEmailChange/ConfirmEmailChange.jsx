import { useState } from "react";
import { Link, useLocation, useSearchParams } from "react-router-dom";
import { getErrorMessage } from "../../api/api";
import { confirmEmailChangeAPI } from "../../api/accountEmailAPI";
import { useAuthToken } from "../../utils/auth";
import { useSession } from "../../components/useSession";
import "../Auth/Auth.css";

// Opened from the email sent to a new address (/confirm-email?token=...). The change only
// completes for the account that asked for it, so this asks you to sign in first (the request
// comes back here afterwards) and then waits for a click: the link is not used up by an email
// scanner or a prefetch.
function ConfirmEmailChange() {
    const [params] = useSearchParams();
    const location = useLocation();
    const token = params.get("token") ?? "";
    const authToken = useAuthToken();
    const { status, retry } = useSession();
    const [submitting, setSubmitting] = useState(false);
    const [result, setResult] = useState(null);

    const handleConfirm = async () => {
        setSubmitting(true);
        try {
            const data = await confirmEmailChangeAPI(token);
            setResult({ ok: true, message: `Your email address is now ${data.email}. Use it to log in from now on.` });
        } catch (err) {
            setResult({ ok: false, message: getErrorMessage(err, "Could not confirm the new email address") });
        }
        setSubmitting(false);
    };

    const renderBody = () => {
        if (!token) {
            return <div className="auth-alert auth-alert-top danger" role="alert">This link is incomplete. Open the link from the email again.</div>;
        }
        if (status === "pending") {
            return <p className="auth-subtitle" role="status">Checking your session…</p>;
        }
        if (status === "unreachable") {
            return (
                <>
                    <div className="auth-alert auth-alert-top danger" role="alert">Can&apos;t reach the server.</div>
                    <button type="button" className="auth-secondary-button" onClick={retry}>Retry</button>
                </>
            );
        }
        if (status === "anonymous" || !authToken) {
            return (
                <>
                    <p className="auth-subtitle">
                        Log in to confirm your new email address. Use the account that asked for the change, with its
                        current email address.
                    </p>
                    <Link
                        to="/login"
                        state={{ from: `${location.pathname}${location.search}` }}
                        className="auth-button auth-button-link"
                    >
                        Log in
                    </Link>
                </>
            );
        }
        if (result) {
            return (
                <>
                    <div className={`auth-alert auth-alert-top ${result.ok ? "success" : "danger"}`} role={result.ok ? "status" : "alert"}>
                        {result.message}
                    </div>
                    <Link to="/dashboard" className="auth-button auth-button-link">Go to your files</Link>
                </>
            );
        }
        return (
            <>
                <p className="auth-subtitle">Confirm that this is the new email address for your FileVault account.</p>
                <button type="button" className="auth-button" disabled={submitting} onClick={handleConfirm}>
                    {submitting ? "Confirming…" : "Confirm new email address"}
                </button>
            </>
        );
    };

    return (
        <div className="auth-page">
            <div className="auth-card">
                <h1 className="auth-title">Confirm new email</h1>
                {renderBody()}
            </div>
        </div>
    );
}

export default ConfirmEmailChange;
