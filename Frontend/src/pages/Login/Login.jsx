import React, { useEffect, useState } from "react";
import "./Login.css";
import { login } from "../../services/Auth";
import { setToken } from "../../utils/auth";
import ServerStatus from "../../components/ServerStatus";
import { useLocation, useNavigate } from "react-router-dom";

function Login() {
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [errors, setErrors] = useState({});
    const [loginStatus, setLoginStatus] = useState(null);
    const location = useLocation();
    const [regStatus, setRegStatus] = useState(false);
    const registrationSuccess = location.state?.registrationSuccess;
    const navigate = useNavigate();

    useEffect(() => {
        registrationSuccess && setRegStatus(true);
    }, [registrationSuccess]);

    const validateForm = () => {
        const newErrors = {};
        if (!email) newErrors.email = "Email is required";
        else if (!/\S+@\S+\.\S+/.test(email))
            newErrors.email = "Email is invalid";
        // Only check it's present: older accounts may predate the current password rules
        if (!password) newErrors.password = "Password is required";
        return newErrors;
    };

    const handleSubmit = async (event) => {
        event.preventDefault();
        setRegStatus(false);
        const formErrors = validateForm();

        if (Object.keys(formErrors).length > 0) {
            setErrors(formErrors);
        } else {
            setErrors({});
            const response = await login(email, password);

            if (response.status === 200) {
                setToken(response.data.success);
                navigate("/dashboard", { replace: true });
            } else {
                // e.g. "Invalid email or password" or the rate-limit message
                setLoginStatus(response.data?.error || "Login failed");
            }
        }
    };

    return (
        <div className="login-wrapper">
            <div className="login-form-container">
                <ServerStatus />
                <h2 className="login-title">Login</h2>
                <form onSubmit={handleSubmit} className="login-form">
                    <div className="form-group">
                        <label>Email address</label>
                        <input
                            type="email"
                            placeholder="Enter email"
                            value={email}
                            onChange={(e) => setEmail(e.target.value)}
                            className={errors.email ? "input-error" : ""}
                        />
                        {errors.email && <div className="error-message">{errors.email}</div>}
                    </div>

                    <div className="form-group">
                        <label>Password</label>
                        <input
                            type="password"
                            placeholder="Password"
                            value={password}
                            onChange={(e) => setPassword(e.target.value)}
                            className={errors.password ? "input-error" : ""}
                        />
                        {errors.password && <div className="error-message">{errors.password}</div>}
                    </div>

                    <button type="submit" className="login-button">Login</button>

                    {regStatus && <div className="alert success">Registration Successful</div>}
                    {loginStatus && <div className="alert danger">{loginStatus}</div>}
                </form>
            </div>
        </div>
    );
}

export default Login;
