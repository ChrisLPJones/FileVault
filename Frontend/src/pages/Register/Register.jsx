import { useState } from "react";
import { Form, Button, Alert } from "react-bootstrap";
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
    const [registerStatus, setLoginStatus] = useState(null);
    const [returnMessage, setReturnMessage] = useState("");
    const [showPasswordRequirements, setShowPasswordRequirements] =
        useState(false);
    const [passwordValid, setPasswordValid] = useState(false);

    const navigate = useNavigate();

    // Password requirement check
    const checkPasswordRequirements = (pass) => {
        const lengthOK = pass.length >= 6;
        const hasNumber = /\d/.test(pass);
        const hasUpper = /[A-Z]/.test(pass);

        setPasswordValid(lengthOK && hasNumber && hasUpper);
        return lengthOK && hasNumber && hasUpper;
    };

    // Live password match check
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

    // Submit validation
    const validateForm = () => {
        const newErrors = {};

        if (!username) newErrors.username = "Username is required"

        else if (!email) newErrors.email = "Email is required";
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
                setLoginStatus(true);
                navigate("/login", { state: { registrationSuccess: true } });
            } else {
                setReturnMessage(response.data.error);
                setLoginStatus(false);
            }
        } catch (err) {
            setLoginStatus(false);
            console.log(err);
        }
    };

    return (
        <div className="register-wrapper">
            <div className="register-form-container">
                <ServerStatus />
                <h2 className="register-title">Register</h2>

                <Form onSubmit={handleSubmit} className="register-form">
                    {/* USERNAME */}
                    <Form.Group className="mb-3" controlId="formBasicUsername">
                        <Form.Label>Username</Form.Label>
                        <Form.Control
                            type="text"
                            placeholder="Enter username"
                            value={username}
                            onChange={(e) => setUsername(e.target.value)}
                            isInvalid={!!errors.username}
                        />
                        <Form.Control.Feedback type="invalid">
                            {errors.username}
                        </Form.Control.Feedback>
                    </Form.Group>

                    {/* EMAIL */}
                    <Form.Group className="mb-3" controlId="formBasicEmail">
                        <Form.Label>Email address</Form.Label>
                        <Form.Control
                            type="email"
                            placeholder="Enter email"
                            value={email}
                            onChange={(e) => setEmail(e.target.value)}
                            isInvalid={!!errors.email}
                        />
                        <Form.Control.Feedback type="invalid">
                            {errors.email}
                        </Form.Control.Feedback>
                    </Form.Group>

                    {/* PASSWORD */}
                    <Form.Group className="mb-3" controlId="formBasicPassword">
                        <Form.Label>Password</Form.Label>
                        <Form.Control
                            type="password"
                            placeholder="Enter password"
                            value={password}
                            onChange={(e) => {
                                const value = e.target.value;
                                setPassword(value);
                                validatePasswordMatch(value, passwordVerify);
                            }}
                            isInvalid={!!errors.password}
                        />

                        

                        <Form.Control.Feedback type="invalid">
                            {errors.password}
                        </Form.Control.Feedback>
                    </Form.Group>

                    {/* PASSWORD VERIFY */}
                    <Form.Group
                        className="mb-3"
                        controlId="formBasicPasswordVerify"
                    >
                        <Form.Control
                            type="password"
                            placeholder="Repeat your password"
                            value={passwordVerify}
                            onChange={(e) => {
                                setPasswordVerify(e.target.value);
                                validatePasswordMatch(password, e.target.value);
                            }}
                            isInvalid={!!errors.passwordVerify}
                        />
                        {/* PASSWORD REQUIREMENTS & MATCH MESSAGE */}
                        <div style={{ fontSize: "0.9rem", marginTop: "5px" }}>
                            <ul
                                style={{ paddingLeft: "20px", margin: "2px 0" }}
                            >
                                <li
                                    style={{
                                        color:
                                            password.length >= 6
                                                ? "green"
                                                : "red",
                                    }}
                                >
                                    At least 6 characters
                                </li>
                                <li
                                    style={{
                                        color: /\d/.test(password)
                                            ? "green"
                                            : "red",
                                    }}
                                >
                                    At least one number
                                </li>
                                <li
                                    style={{
                                        color: /[A-Z]/.test(password)
                                            ? "green"
                                            : "red",
                                    }}
                                >
                                    At least one uppercase letter
                                </li>
                                {/* Show password match if available */}
                                {passwordMatchValid !== null && (
                                    <li
                                        style={{
                                            color: passwordMatchValid
                                                ? "green"
                                                : "red",
                                        }}
                                    >
                                        {passwordMatchMessage}
                                    </li>
                                )}
                            </ul>
                        </div>
                        <Form.Control.Feedback type="invalid">
                            {errors.passwordVerify}
                        </Form.Control.Feedback>
                    </Form.Group>

                    {/* SUBMIT BUTTON */}
                    <Button
                        variant="primary"
                        type="submit"
                        className="register-button"
                    >
                        Register
                    </Button>

                    {/* LOGIN STATUS MESSAGE */}
                    {registerStatus !== null &&
                        (registerStatus ? (
                            <Alert className="register-alert" variant="success">
                                {returnMessage}
                            </Alert>
                        ) : (
                            <Alert className="register-alert" variant="danger">
                                {returnMessage}
                            </Alert>
                        ))}
                </Form>
            </div>
        </div>
    );
}

export default Register;
