import { useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { getErrorMessage } from "../../api/api";
import { resetPasswordAPI } from "../../api/accountEmailAPI";
import { meetsPasswordRules, passwordRules } from "../../utils/passwordRules";
import { clearToken } from "../../utils/auth";
import "../Auth/Auth.css";

const ruleClass = (typed, met) => (!typed ? undefined : met ? "met" : "unmet");

// Opened from the reset email (/reset-password?token=...): choose a new password
function ResetPassword() {
    const [params] = useSearchParams();
    const token = params.get("token") ?? "";
    const [password, setPassword] = useState("");
    const [confirm, setConfirm] = useState("");
    const [error, setError] = useState(null);
    const [done, setDone] = useState(false);
    const [submitting, setSubmitting] = useState(false);

    const handleSubmit = async (event) => {
        event.preventDefault();
        if (!meetsPasswordRules(password)) {
            setError("The password doesn't meet the requirements");
            return;
        }
        if (password !== confirm) {
            setError("Passwords do not match");
            return;
        }

        setSubmitting(true);
        setError(null);
        try {
            await resetPasswordAPI(token, password);
            // Every session was signed out, including any in this browser
            clearToken();
            setDone(true);
        } catch (err) {
            setError(getErrorMessage(err, "Could not reset the password"));
        } finally {
            setSubmitting(false);
        }
    };

    const body = () => {
        if (!token) {
            return (
                <div className="auth-alert auth-alert-top danger" role="alert">
                    This link is incomplete. Open the link from the email again, or ask for a new one.
                </div>
            );
        }

        if (done) {
            return (
                <>
                    <div className="auth-alert auth-alert-top success" role="status">
                        Your password has been changed and you've been signed out everywhere.
                    </div>
                    <Link to="/login" className="auth-button auth-link-button">Log in</Link>
                </>
            );
        }

        return (
            <form onSubmit={handleSubmit} noValidate>
                <div className="auth-field">
                    <label htmlFor="reset-password">New password</label>
                    <input
                        id="reset-password"
                        type="password"
                        autoComplete="new-password"
                        value={password}
                        onChange={(e) => {
                            setPassword(e.target.value);
                            setError(null);
                        }}
                    />
                    <ul className="auth-rules" aria-label="Password requirements">
                        {passwordRules.map((rule) => (
                            <li key={rule.label} className={ruleClass(password.length > 0, rule.test(password))}>
                                {rule.label}
                            </li>
                        ))}
                    </ul>
                </div>
                <div className="auth-field">
                    <label htmlFor="reset-password-confirm">Confirm new password</label>
                    <input
                        id="reset-password-confirm"
                        type="password"
                        autoComplete="new-password"
                        value={confirm}
                        onChange={(e) => {
                            setConfirm(e.target.value);
                            setError(null);
                        }}
                    />
                </div>
                <button type="submit" className="auth-button" disabled={submitting}>
                    {submitting ? "Saving…" : "Set new password"}
                </button>
                {error && <div className="auth-alert danger" role="alert">{error}</div>}
            </form>
        );
    };

    return (
        <div className="auth-page">
            <div className="auth-card">
                <h1 className="auth-title">Choose a new password</h1>
                <p className="auth-subtitle">For your FileVault account</p>
                {body()}
                {!done && (
                    <p className="auth-switch">
                        Link expired? <Link to="/forgot-password">Get a new one</Link>
                    </p>
                )}
            </div>
        </div>
    );
}

export default ResetPassword;
