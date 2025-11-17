// Login.js:
import React, { useState } from "react";
import { Form, Button, Container, Row, Col, Alert } from "react-bootstrap";
import "./register.css";
import { login } from "../../services/Auth";
import ServerStatus from "../../components/ServerStatus";

function Register() {
    const [username, setUsername] = useState("");
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [passwordVerify, setPasswordVerify] = useState("");
    const [showPassword, setShowPassword] = useState(false);
    const [showPasswordVerify, setShowPasswordVerify] = useState(false);
    const [errors, setErrors] = useState({});
    const [loginStatus, setLoginStatus] = useState(null);

    const validateForm = () => {
        const newErrors = {};
        if (!email) newErrors.email = "Email is required";
        else if (!/\S+@\S+\.\S+/.test(email))
            newErrors.email = "Email is invalid";
        if (!password) newErrors.password = "Password is required";
        else if (password.length < 6)
            newErrors.password = "Password must be at least 6 characters";
        else if (password != passwordVerify)
            newErrors.passwordVerify = "Passwords do not match!";
        return newErrors;
    };

    const handleSubmit = async (event) => {
        event.preventDefault();
        const formErrors = validateForm();
        if (Object.keys(formErrors).length > 0) {
            setErrors(formErrors);
        } else {
            setErrors({});
            const userData = await login(email, password);
            if (userData.status === 200) {
                setLoginStatus(true);
            } else {
                setLoginStatus(false);
            }
        }
    };

    return (
        <div className="register-wrapper">
            <div className="register-form-container">
                <ServerStatus />
                <h2 className="register-title">Register</h2>
                <Form onSubmit={handleSubmit} className="register-form">
                    <Form.Group className="mb-3" controlId="formBasicUsername">
                        <Form.Label>Username</Form.Label>
                        <Form.Control
                            type="username"
                            placeholder="Your username"
                            value={username}
                            onChange={(e) => setUsername(e.target.value)}
                        />
                    </Form.Group>

                    <Form.Group className="mb-3" controlId="formBasicEmail">
                        <Form.Label>Email address</Form.Label>
                        <Form.Control
                            type="email"
                            placeholder="Your Email"
                            value={email}
                            onChange={(e) => setEmail(e.target.value)}
                            isInvalid={!!errors.email}
                        />
                        <Form.Control.Feedback type="invalid">
                            {errors.email}
                        </Form.Control.Feedback>
                    </Form.Group>

                    <Form.Group className="mb-3" controlId="formBasicPassword">
                        <Form.Label>Password</Form.Label>
                        <Form.Control
                            type="password"
                            placeholder="Password"
                            value={password}
                            onChange={(e) => setPassword(e.target.value)}
                            isInvalid={!!errors.password}
                        />
                        <Form.Control.Feedback type="invalid">
                            {errors.password}
                        </Form.Control.Feedback>
                    </Form.Group>

                    <Form.Group
                        className="mb-3"
                        controlId="formBasicPasswordVerify"
                    >
                        <Form.Control
                            type="password"
                            placeholder="Repeat your password"
                            value={passwordVerify}
                            onChange={(e) => setPasswordVerify(e.target.value)}
                            isInvalid={!!errors.password || !!errors.passwordVerify}
                        />
                        <Form.Control.Feedback type="invalid">
                            {errors.password || errors.passwordVerify}
                        </Form.Control.Feedback>
                    </Form.Group>

                    <Button
                        variant="primary"
                        type="submit"
                        className="register-button"
                    >
                        Register
                    </Button>
                    {loginStatus !== null &&
                        (loginStatus ? (
                            <Alert className="register-alert" variant="success">
                                Login Successful
                            </Alert>
                        ) : (
                            <Alert className="register-alert" variant="danger">
                                Login Failed
                            </Alert>
                        ))}
                </Form>
            </div>
        </div>
    );
}

export default Register;
