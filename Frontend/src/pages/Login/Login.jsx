// Login.js:
import React, { useEffect, useState } from "react";
import { Form, Button, Container, Row, Col, Alert } from "react-bootstrap";
import "./Login.css";
import { login } from "../../services/Auth";
import ServerStatus from "../../components/ServerStatus";
import { useLocation } from "react-router-dom";

function Login() {
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [errors, setErrors] = useState({});
    const [loginStatus, setLoginStatus] = useState(null);
    const location = useLocation();
    const [regStatus, setRegStatus] = useState(false);
    const registrationSuccess = location.state?.registrationSuccess;

    useEffect(() => {
        registrationSuccess && setRegStatus(true);
    }, [registrationSuccess]);

    const validateForm = () => {
        const newErrors = {};
        if (!email) newErrors.email = "Email is required";
        else if (!/\S+@\S+\.\S+/.test(email))
            newErrors.email = "Email is invalid";
        if (!password) newErrors.password = "Password is required";
        else if (password.length < 6)
            newErrors.password = "Password must be at least 6 characters";
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
                setLoginStatus(true);
            } else {
                setLoginStatus(false);
            }
        }
    };

    return (
        <div className="login-wrapper">
            <div className="login-form-container">
                <ServerStatus />
                <h2 className="login-title">Login</h2>
                <Form onSubmit={handleSubmit} className="login-form">
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

                    <Button
                        variant="primary"
                        type="submit"
                        className="login-button"
                    >
                        Login
                    </Button>
                    {regStatus && (
                        <Alert className="login-alert" variant="success">
                            Registration Successful
                        </Alert>
                    )}
                    {loginStatus !== null &&
                        (loginStatus ? (
                            <Alert className="login-alert" variant="success">
                                Login Successful
                            </Alert>
                        ) : (
                            <Alert className="login-alert" variant="danger">
                                Login Failed
                            </Alert>
                        ))}
                </Form>
            </div>
        </div>
    );
}

export default Login;
