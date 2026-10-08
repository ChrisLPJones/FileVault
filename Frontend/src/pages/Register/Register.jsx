import { useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import "../Auth/Auth.css";
import { register } from "../../services/Auth";
import ServerStatus from "../../components/ServerStatus";
import { meetsPasswordRules, passwordRules } from "../../utils/passwordRules";

// Rule list item: neutral until the user starts typing, then green/red
const ruleClass = (typed, passed) => (!typed ? undefined : passed ? "met" : "unmet");

function Register() {
    const [firstName, setFirstName] = useState("");
    const [lastName, setLastName] = useState("");
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [passwordVerify, setPasswordVerify] = useState("");
    const [errors, setErrors] = useState({});
    const [registerError, setRegisterError] = useState(null);
    const [submitting, setSubmitting] = useState(false);
    const navigate = useNavigate();

    const passwordsMatch = passwordVerify.length > 0 && password === passwordVerify;

    // Update a field and clear its error, so stale messages don't linger while typing
    const edit = (setter, name) => (event) => {
        setter(event.target.value);
        if (errors[name]) setErrors(({ ...rest }) => { delete rest[name]; return rest; });
    };

    const validateForm = () => {
        const newErrors = {};

        if (!firstName.trim()) newErrors.firstName = "First name is required";
        else if (firstName.trim().length > 50) newErrors.firstName = "First name must be 50 characters or fewer";

        if (lastName.trim().length > 50) newErrors.lastName = "Last name must be 50 characters or fewer";

        if (!email) newErrors.email = "Email is required";
        else if (!/\S+@\S+\.\S+/.test(email)) newErrors.email = "Email is invalid";

        if (!password) newErrors.password = "Password is required";
        else if (!meetsPasswordRules(password)) newErrors.password = "Password doesn't meet the requirements below";

        if (!passwordVerify) newErrors.passwordVerify = "Please confirm your password";
        else if (password !== passwordVerify) newErrors.passwordVerify = "Passwords do not match";

        return newErrors;
    };

    const handleSubmit = async (event) => {
        event.preventDefault();
        setRegisterError(null);

        const formErrors = validateForm();
        setErrors(formErrors);
        if (Object.keys(formErrors).length > 0) return;

        setSubmitting(true);
        try {
            const response = await register(firstName.trim(), lastName.trim(), email.trim(), password);
            if (response?.status === 200) {
                navigate("/login", { state: { registrationSuccess: true } });
                return;
            }
            setRegisterError(response.data?.error || "Registration failed");
        } catch {
            setRegisterError("Can't reach the server. Please try again.");
        }
        setSubmitting(false);
    };

    // Shared props for an input with an optional error message below it
    const fieldProps = (name) => ({
        "aria-invalid": !!errors[name],
        "aria-describedby": errors[name] ? `register-${name}-error` : undefined,
    });
    const fieldError = (name) =>
        errors[name] && <div id={`register-${name}-error`} className="auth-field-error">{errors[name]}</div>;

    return (
        <div className="auth-page">
            <div className="auth-card">
                <ServerStatus />
                <h1 className="auth-title">Create account</h1>
                <p className="auth-subtitle">Your files, encrypted and private</p>

                <form onSubmit={handleSubmit} noValidate>
                    <div className="auth-field">
                        <label htmlFor="register-first-name">First name</label>
                        <input
                            id="register-first-name"
                            name="firstName"
                            type="text"
                            autoComplete="given-name"
                            value={firstName}
                            onChange={edit(setFirstName, "firstName")}
                            {...fieldProps("firstName")}
                        />
                        {fieldError("firstName")}
                    </div>

                    <div className="auth-field">
                        <label htmlFor="register-last-name">Last name <span className="auth-optional">(optional)</span></label>
                        <input
                            id="register-last-name"
                            name="lastName"
                            type="text"
                            autoComplete="family-name"
                            value={lastName}
                            onChange={edit(setLastName, "lastName")}
                            {...fieldProps("lastName")}
                        />
                        {fieldError("lastName")}
                    </div>

                    <div className="auth-field">
                        <label htmlFor="register-email">Email address</label>
                        <input
                            id="register-email"
                            name="email"
                            type="email"
                            autoComplete="email"
                            placeholder="you@example.com"
                            value={email}
                            onChange={edit(setEmail, "email")}
                            {...fieldProps("email")}
                        />
                        {fieldError("email")}
                    </div>

                    <div className="auth-field">
                        <label htmlFor="register-password">Password</label>
                        <input
                            id="register-password"
                            name="password"
                            type="password"
                            autoComplete="new-password"
                            value={password}
                            onChange={edit(setPassword, "password")}
                            {...fieldProps("password")}
                        />
                        {fieldError("password")}
                        <ul className="auth-rules" aria-label="Password requirements">
                            {passwordRules.map((rule) => (
                                <li key={rule.label} className={ruleClass(password.length > 0, rule.test(password))}>
                                    {rule.label}
                                </li>
                            ))}
                        </ul>
                    </div>

                    <div className="auth-field">
                        <label htmlFor="register-password-confirm">Confirm password</label>
                        <input
                            id="register-password-confirm"
                            name="passwordConfirm"
                            type="password"
                            autoComplete="new-password"
                            value={passwordVerify}
                            onChange={edit(setPasswordVerify, "passwordVerify")}
                            {...fieldProps("passwordVerify")}
                        />
                        {fieldError("passwordVerify") ||
                            (passwordVerify && (
                                <div className={`auth-field-error ${passwordsMatch ? "auth-match" : ""}`}>
                                    {passwordsMatch ? "Passwords match" : "Passwords do not match"}
                                </div>
                            ))}
                    </div>

                    <button type="submit" className="auth-button" disabled={submitting}>
                        {submitting ? "Creating account…" : "Create account"}
                    </button>

                    {registerError && <div className="auth-alert danger" role="alert">{registerError}</div>}
                </form>

                <p className="auth-switch">
                    Already have an account? <Link to="/login">Log in</Link>
                </p>
            </div>
        </div>
    );
}

export default Register;
