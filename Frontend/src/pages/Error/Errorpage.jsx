import React from "react";
import { Link } from "react-router-dom";
import "./Errorpage.css";

const ErrorPage = () => {
    return (
        <div className="notfound-page">
            <div className="notfound-card">
                <h1 className="notfound-code">404</h1>
                <p className="notfound-message">
                    Oops! The page you are looking for does not exist.
                </p>
                <Link to="/" className="notfound-button">
                    Go Back Home
                </Link>
            </div>
        </div>
    );
};

export default ErrorPage;
