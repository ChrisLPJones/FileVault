import { useState } from "react";
import { Link } from "react-router-dom";
import { getErrorMessage } from "../../api/api";
import { forgotPasswordAPI } from "../../api/accountEmailAPI";
import "../Auth/Auth.css";

// Asks for an email address and sends a reset link to it (if it has an account)
function ForgotPassword() {
    const [email, setEmail] = useState("");
    const [error, setError] = useState(null);
    const [sentMessage, setSentMessage] = useState(null);
    const [submitting, setSubmitting] = useState(false);

    const handleSubmit = async (event) => {
        event.preventDefault();
        if (!email.trim()) {
            setError("Enter your email address");
            return;
        }

        setSubmitting(true);
        setError(null);
        try {
            const result = await forgotPasswordAPI(email.trim());
            setSentMessage(result.success);
        } catch (err) {
            setError(getErrorMessage(err, "Can't reach the server. Please try again."));
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <div className="auth-page">
            <div className="auth-card">
                <h1 className="auth-title">Forgot password</h1>
                <p className="auth-subtitle">We'll email you a link to choose a new one</p>

                {sentMessage ? (
                    <div className="auth-alert auth-alert-top success" role="status">
                        {sentMessage} It expires in an hour. Check your spam folder if it doesn't arrive.
                    </div>
                ) : (
                    <form onSubmit={handleSubmit} noValidate>
                        <div className="auth-field">
                            <label htmlFor="forgot-email">Email address</label>
                            <input
                                id="forgot-email"
                                type="email"
                                autoComplete="email"
                                autoCapitalize="none"
                                spellCheck={false}
                                value={email}
                                onChange={(e) => {
                                    setEmail(e.target.value);
                                    setError(null);
                                }}
                                aria-invalid={!!error}
                                aria-describedby={error ? "forgot-email-error" : undefined}
                            />
                            {error && <div id="forgot-email-error" className="auth-field-error">{error}</div>}
                        </div>
                        <button type="submit" className="auth-button" disabled={submitting}>
                            {submitting ? "Sending…" : "Send reset link"}
                        </button>
                    </form>
                )}

                <p className="auth-switch">
                    Remembered it? <Link to="/login">Log in</Link>
                </p>
            </div>
        </div>
    );
}

export default ForgotPassword;
