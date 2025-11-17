import React from "react";
import { Link } from "react-router-dom";
import "./Error-page.css";

const ErrorPage = () => {
  return (
    <div className="error-container">
      <div className="error-content">
        <h1 className="error-code">404</h1>
        <p className="error-message">Oops! The page you are looking for does not exist.</p>
        <Link to="/" className="error-button">
          Go Back Home
        </Link>
      </div>
    </div>
  );
};

export default ErrorPage;
