import { useState } from "react";
import { Link, useLocation, useNavigate } from "react-router-dom";
import "../Auth/Auth.css";
import { login } from "../../services/Auth";
import { startSession } from "../../utils/auth";
import { returnPathAfterLogin } from "../../utils/returnPath";
import ServerStatus from "../../components/ServerStatus";
import { getErrorMessage } from "../../api/api";
import { resendVerificationByEmailAPI } from "../../api/accountEmailAPI";
import TwoFactorStep from "./TwoFactorStep";

function Login() {
    const [identifier, setIdentifier] = useState("");
    const [password, setPassword] = useState("");
    const [errors, setErrors] = useState({});
    const [loginError, setLoginError] = useState(null);
    const [submitting, setSubmitting] = useState(false);
    const [unverifiedEmail, setUnverifiedEmail] = useState(null);
    const [suspended, setSuspended] = useState(false); // right password, but an administrator suspended the account
    const [resendStatus, setResendStatus] = useState(null); // null | "sending" | message
    // Set when the password was right but two-factor authentication needs a code
    const [challengeToken, setChallengeToken] = useState(null);
    const location = useLocation();
    const navigate = useNavigate();
    // "Account created" shows after arriving from the register page, until the first login attempt
    const [regMessageDismissed, setRegMessageDismissed] = useState(false);
    const showRegistered = location.state?.registrationSuccess && !regMessageDismissed;

    // Update a field and clear its error, so stale messages don't linger while typing
    const edit = (setter, name) => (event) => {
        setter(event.target.value);
        if (errors[name]) setErrors(({ ...rest }) => { delete rest[name]; return rest; });
    };

    const validateForm = () => {
        const newErrors = {};
        if (!identifier.trim()) newErrors.identifier = "Enter your email address";
        // Only check it's present: older accounts may predate the current password rules
        if (!password) newErrors.password = "Password is required";
        return newErrors;
    };

    const handleSubmit = async (event) => {
        event.preventDefault();
        setRegMessageDismissed(true);
        setLoginError(null);
        setUnverifiedEmail(null);
        setResendStatus(null);

        const formErrors = validateForm();
        setErrors(formErrors);
        if (Object.keys(formErrors).length > 0) return;

        setSubmitting(true);
        try {
            const response = await login(identifier.trim(), password);
            if (response.status === 200 && response.data?.twoFactorRequired) {
                setPassword("");
                setSubmitting(false);
                setChallengeToken(response.data.challengeToken);
                return;
            }
            if (response.status === 200) {
                startSession(response.data.success);
                navigate(returnPathAfterLogin(location.state), { replace: true });
                return;
            }
            // Right password, but the address hasn't been confirmed yet: offer to resend the link
            setUnverifiedEmail(response.data?.emailNotVerified ? identifier.trim() : null);
            setSuspended(response.data?.suspended === true);
            // e.g. "Invalid email or password" or the rate-limit message
            setLoginError(response.data?.error || "Login failed");
        } catch {
            setLoginError("Can't reach the server. Please try again.");
        }
        setSubmitting(false);
    };

    const handleResend = async () => {
        setResendStatus("sending");
        try {
            setResendStatus((await resendVerificationByEmailAPI(unverifiedEmail)).success);
        } catch (err) {
            setResendStatus(getErrorMessage(err, "Could not send the email. Please try again."));
        }
    };
    if (challengeToken) {
        return (
            <TwoFactorStep
                challengeToken={challengeToken}
                onSuccess={(token) => {
                    startSession(token);
                    navigate(returnPathAfterLogin(location.state), { replace: true });
                }}
                onRestart={() => setChallengeToken(null)}
            />
        );
    }

    return (
        <div className="auth-page">
            <div className="auth-card">
                <ServerStatus />
                <h1 className="auth-title">Log in</h1>
                <p className="auth-subtitle">Welcome back to FileVault</p>

                {showRegistered && (
                    <div className="auth-alert auth-alert-top success" role="status">
                        Account created. We've emailed you a link to confirm your address: open it, then log in.
                    </div>
                )}

                <form onSubmit={handleSubmit} noValidate>
                    <div className="auth-field">
                        <label htmlFor="login-identifier">Email address</label>
                        <input
                            id="login-identifier"
                            name="email"
                            type="email"
                            autoComplete="email"
                            autoCapitalize="none"
                            spellCheck={false}
                            value={identifier}
                            onChange={edit(setIdentifier, "identifier")}
                            aria-invalid={!!errors.identifier}
                            aria-describedby={errors.identifier ? "login-identifier-error" : undefined}
                        />
                        {errors.identifier && <div id="login-identifier-error" className="auth-field-error">{errors.identifier}</div>}
                    </div>

                    <div className="auth-field">
                        <label htmlFor="login-password">Password</label>
                        <input
                            id="login-password"
                            name="password"
                            type="password"
                            autoComplete="current-password"
                            value={password}
                            onChange={edit(setPassword, "password")}
                            aria-invalid={!!errors.password}
                            aria-describedby={errors.password ? "login-password-error" : undefined}
                        />
                        {errors.password && <div id="login-password-error" className="auth-field-error">{errors.password}</div>}
                        <Link to="/forgot-password" className="auth-forgot">Forgot password?</Link>
                    </div>

                    <button type="submit" className="auth-button" disabled={submitting}>
                        {submitting ? "Logging in…" : "Log in"}
                    </button>

                    {loginError && (
                        <div className="auth-alert danger" role="alert">
                            {loginError}
                            {unverifiedEmail && (
                                <p className="auth-alert-detail">
                                    Open the link we emailed to {unverifiedEmail}, then log in.
                                </p>
                            )}
                            {suspended && (
                                <p className="auth-alert-detail">
                                    Your files are kept. Contact an administrator to get access back.
                                </p>
                            )}
                        </div>
                    )}
                    {unverifiedEmail && (
                        resendStatus && resendStatus !== "sending" ? (
                            <div className="auth-alert success" role="status">{resendStatus}</div>
                        ) : (
                            <button
                                type="button"
                                className="auth-secondary-button"
                                disabled={resendStatus === "sending"}
                                onClick={handleResend}
                            >
                                {resendStatus === "sending" ? "Sending…" : "Resend confirmation email"}
                            </button>
                        )
                    )}
                </form>

                <p className="auth-switch">
                    Don't have an account? <Link to="/register">Create one</Link>
                </p>
            </div>
        </div>
    );
}

export default Login;
