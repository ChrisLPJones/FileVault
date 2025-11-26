import { useState } from "react";
import "./register.css";
import { register } from "../../services/Auth";
import ServerStatus from "../../components/ServerStatus";
import { useNavigate } from "react-router-dom";

function Register() {
    const [username, setUsername] = useState("");
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [passwordVerify, setPasswordVerify] = useState("");
    const [passwordMatchMessage, setPasswordMatchMessage] = useState("");
    const [passwordMatchValid, setPasswordMatchValid] = useState(null);
    const [errors, setErrors] = useState({});
    const [registerStatus, setRegisterStatus] = useState(null);
    const [returnMessage, setReturnMessage] = useState("");
    const [passwordValid, setPasswordValid] = useState(false);

    const navigate = useNavigate();

    const checkPasswordRequirements = (pass) => {
        const lengthOK = pass.length >= 6;
        const hasNumber = /\d/.test(pass);
        const hasUpper = /[A-Z]/.test(pass);

        setPasswordValid(lengthOK && hasNumber && hasUpper);
        return lengthOK && hasNumber && hasUpper;
    };

    const validatePasswordMatch = (pass, passVerify) => {
        if (!passVerify) {
            setPasswordMatchMessage("");
            setPasswordMatchValid(null);
            return;
        }

        if (pass === passVerify) {
            setPasswordMatchMessage("Passwords match!");
            setPasswordMatchValid(true);
        } else {
            setPasswordMatchMessage("Passwords do not match!");
            setPasswordMatchValid(false);
        }
    };

    const validateForm = () => {
        const newErrors = {};

        if (!username) newErrors.username = "Username is required";
        if (!email) newErrors.email = "Email is required";
        else if (!/\S+@\S+\.\S+/.test(email))
            newErrors.email = "Email is invalid";

        if (!password) newErrors.password = "Password is required";
        else if (password.length < 6)
            newErrors.password = "Password must be at least 6 characters";

        if (password !== passwordVerify)
            newErrors.passwordVerify = "Passwords do not match!";

        return newErrors;
    };

    const handleSubmit = async (event) => {
        event.preventDefault();
        const formErrors = validateForm();

        if (Object.keys(formErrors).length > 0) {
            setErrors(formErrors);
            return;
        }

        setErrors({});

        try {
            const response = await register(username, email, password);
            setReturnMessage("");

            if (response?.status === 200) {
                setReturnMessage(response.data.success);
                setRegisterStatus(true);
                navigate("/login", { state: { registrationSuccess: true } });
            } else {
                setReturnMessage(response.data.error);
                setRegisterStatus(false);
            }
        } catch (err) {
            setRegisterStatus(false);
            console.log(err);
        }
    };

    return (
        <div className="register-wrapper">
            <div className="register-form-container">
                <ServerStatus />
                <h2 className="register-title">Register</h2>

                <form onSubmit={handleSubmit} className="register-form">

                    {/* USERNAME */}
                    <div className="form-group">
                        <label>Username</label>
                        <input
                            type="text"
                            placeholder="Enter username"
                            value={username}
                            onChange={(e) => {
                                setUsername(e.target.value)
                                setRegisterStatus(null)}
                            }
                            className={errors.username ? "input-error" : ""}
                        />
                        {errors.username && (
                            <div className="error-message">{errors.username}</div>
                        )}
                    </div>

                    {/* EMAIL */}
                    <div className="form-group">
                        <label>Email address</label>
                        <input
                            type="email"
                            placeholder="Enter email"
                            value={email}
                            onChange={(e) =>{ 
                                setEmail(e.target.value)
                                setRegisterStatus(null)
                            }
                                
                            }
                            className={errors.email ? "input-error" : ""}
                        />
                        {errors.email && (
                            <div className="error-message">{errors.email}</div>
                        )}
                    </div>

                    {/* PASSWORD */}
                    <div className="form-group">
                        <label>Password</label>
                        <input
                            type="password"
                            placeholder="Enter password"
                            value={password}
                            onChange={(e) => {
                                const value = e.target.value;
                                setPassword(value);
                                checkPasswordRequirements(value);
                                validatePasswordMatch(value, passwordVerify);
                            }}
                            className={errors.password ? "input-error" : ""}
                        />
                        {errors.password && (
                            <div className="error-message">{errors.password}</div>
                        )}
                    </div>

                    {/* PASSWORD VERIFY */}
                    <div className="form-group">
                        <input
                            type="password"
                            placeholder="Repeat your password"
                            value={passwordVerify}
                            onChange={(e) => {
                                setPasswordVerify(e.target.value);
                                validatePasswordMatch(password, e.target.value);
                            }}
                            className={errors.passwordVerify ? "input-error" : ""}
                        />

                        {/* PASSWORD RULES + MATCH */}
                        <ul className="password-rules">
                            <li style={{ color: password.length >= 6 ? "green" : "red" }}>
                                At least 6 characters
                            </li>
                            <li style={{ color: /\d/.test(password) ? "green" : "red" }}>
                                At least one number
                            </li>
                            <li style={{ color: /[A-Z]/.test(password) ? "green" : "red" }}>
                                At least one uppercase letter
                            </li>

                            {passwordMatchValid !== null && (
                                <li
                                    style={{
                                        color: passwordMatchValid ? "green" : "red",
                                    }}
                                >
                                    {passwordMatchMessage}
                                </li>
                            )}
                        </ul>

                        {/* {errors.passwordVerify && (
                            <div className="error-message">{errors.passwordVerify}</div>
                        )} */}
                    </div>

                    {/* SUBMIT BUTTON */}
                    <button type="submit" className="register-button">
                        Register
                    </button>

                    {/* RESULT MESSAGE */}
                    {registerStatus !== null && (
                        <div
                            className={
                                registerStatus
                                    ? "register-alert success"
                                    : "register-alert danger"
                            }
                        >
                            {returnMessage}
                        </div>
                    )}
                </form>
            </div>
        </div>
    );
}

export default Register;
