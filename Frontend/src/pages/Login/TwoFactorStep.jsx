import { useState } from "react";
import PropTypes from "prop-types";
import "../Auth/Auth.css";
import { loginTwoFactor } from "../../services/Auth";

// Second login step, shown in place of the login form when two-factor authentication is on:
// a code from the authenticator app, or a recovery code
function TwoFactorStep({ challengeToken, onSuccess, onRestart }) {
    const [useRecoveryCode, setUseRecoveryCode] = useState(false);
    const [code, setCode] = useState("");
    const [error, setError] = useState(null);
    // The challenge has expired or run out of attempts: the password is needed again
    const [expired, setExpired] = useState(false);
    const [submitting, setSubmitting] = useState(false);

    const handleSubmit = async (event) => {
        event.preventDefault();
        setError(null);
        if (!code.trim()) {
            setError(useRecoveryCode ? "Enter a recovery code" : "Enter the 6-digit code");
            return;
        }

        setSubmitting(true);
        try {
            const response = await loginTwoFactor(challengeToken,
                useRecoveryCode ? { recoveryCode: code.trim() } : { code: code.trim() });
            if (response.status === 200) {
                onSuccess(response.data.success);
                return;
            }
            const message = response.data?.error || "Login failed";
            setExpired(response.status === 401 && /log in again/i.test(message));
            setError(message);
            setCode("");
        } catch {
            setError("Can't reach the server. Please try again.");
        }
        setSubmitting(false);
    };

    const toggleMode = () => {
        setUseRecoveryCode(!useRecoveryCode);
        setCode("");
        setError(null);
    };

    return (
        <div className="auth-page">
            <div className="auth-card">
                <h1 className="auth-title">Two-factor authentication</h1>
                <p className="auth-subtitle">
                    {useRecoveryCode
                        ? "Enter one of the recovery codes you saved"
                        : "Enter the code from your authenticator app"}
                </p>

                {expired ? (
                    <>
                        <div className="auth-alert danger auth-alert-top" role="alert">{error}</div>
                        <button type="button" className="auth-button" onClick={onRestart}>Back to log in</button>
                    </>
                ) : (
                    <form onSubmit={handleSubmit} noValidate>
                        <div className="auth-field">
                            {useRecoveryCode ? (
                                <>
                                    <label htmlFor="login-recovery-code">Recovery code</label>
                                    <input
                                        id="login-recovery-code"
                                        type="text"
                                        autoComplete="off"
                                        autoCapitalize="none"
                                        spellCheck={false}
                                        placeholder="xxxx-xxxx-xxxx"
                                        value={code}
                                        onChange={(e) => setCode(e.target.value)}
                                        autoFocus
                                    />
                                </>
                            ) : (
                                <>
                                    <label htmlFor="login-code">6-digit code</label>
                                    <input
                                        id="login-code"
                                        className="auth-code-input"
                                        type="text"
                                        inputMode="numeric"
                                        autoComplete="one-time-code"
                                        maxLength={7}
                                        placeholder="123456"
                                        value={code}
                                        onChange={(e) => setCode(e.target.value)}
                                        autoFocus
                                    />
                                </>
                            )}
                        </div>

                        <button type="submit" className="auth-button" disabled={submitting}>
                            {submitting ? "Checking…" : "Verify"}
                        </button>

                        {error && <div className="auth-alert danger" role="alert">{error}</div>}
                    </form>
                )}

                {!expired && (
                    <p className="auth-switch">
                        <button type="button" className="auth-link-button" onClick={toggleMode}>
                            {useRecoveryCode ? "Use a code from your app" : "Use a recovery code"}
                        </button>
                        {" · "}
                        <button type="button" className="auth-link-button" onClick={onRestart}>
                            Back to log in
                        </button>
                    </p>
                )}
            </div>
        </div>
    );
}

TwoFactorStep.propTypes = {
    challengeToken: PropTypes.string.isRequired,
    onSuccess: PropTypes.func.isRequired,
    onRestart: PropTypes.func.isRequired,
};

export default TwoFactorStep;
