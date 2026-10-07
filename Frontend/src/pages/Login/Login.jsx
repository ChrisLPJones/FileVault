import { useState } from "react";
import { Link, useLocation, useNavigate } from "react-router-dom";
import "../Auth/Auth.css";
import { login } from "../../services/Auth";
import { setToken } from "../../utils/auth";
import ServerStatus from "../../components/ServerStatus";

function Login() {
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [errors, setErrors] = useState({});
    const [loginError, setLoginError] = useState(null);
    const [submitting, setSubmitting] = useState(false);
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
        if (!email) newErrors.email = "Email is required";
        else if (!/\S+@\S+\.\S+/.test(email)) newErrors.email = "Email is invalid";
        // Only check it's present: older accounts may predate the current password rules
        if (!password) newErrors.password = "Password is required";
        return newErrors;
    };

    const handleSubmit = async (event) => {
        event.preventDefault();
        setRegMessageDismissed(true);
        setLoginError(null);

        const formErrors = validateForm();
        setErrors(formErrors);
        if (Object.keys(formErrors).length > 0) return;

        setSubmitting(true);
        try {
            const response = await login(email, password);
            if (response.status === 200) {
                setToken(response.data.success);
                navigate("/dashboard", { replace: true });
                return;
            }
            // e.g. "Invalid email or password" or the rate-limit message
            setLoginError(response.data?.error || "Login failed");
        } catch {
            setLoginError("Can't reach the server. Please try again.");
        }
        setSubmitting(false);
    };

    return (
        <div className="auth-page">
            <div className="auth-card">
                <ServerStatus />
                <h1 className="auth-title">Log in</h1>
                <p className="auth-subtitle">Welcome back to FileVault</p>

                {showRegistered && (
                    <div className="auth-alert auth-alert-top success" role="status">
                        Account created. You can log in now.
                    </div>
                )}

                <form onSubmit={handleSubmit} noValidate>
                    <div className="auth-field">
                        <label htmlFor="login-email">Email address</label>
                        <input
                            id="login-email"
                            name="email"
                            type="email"
                            autoComplete="email"
                            placeholder="you@example.com"
                            value={email}
                            onChange={edit(setEmail, "email")}
                            aria-invalid={!!errors.email}
                            aria-describedby={errors.email ? "login-email-error" : undefined}
                        />
                        {errors.email && <div id="login-email-error" className="auth-field-error">{errors.email}</div>}
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
                    </div>

                    <button type="submit" className="auth-button" disabled={submitting}>
                        {submitting ? "Logging in…" : "Log in"}
                    </button>

                    {loginError && <div className="auth-alert danger" role="alert">{loginError}</div>}
                </form>

                <p className="auth-switch">
                    Don't have an account? <Link to="/register">Create one</Link>
                </p>
            </div>
        </div>
    );
}

export default Login;
