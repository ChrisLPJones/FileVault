import { Navigate } from "react-router-dom";
import { isAuthenticated } from "../utils/auth";

// Login/register pages: send logged-in users to the dashboard
const PublicRoute = ({ children }) => {
    if (isAuthenticated()) {
        return <Navigate to="/dashboard" replace />;
    }

    return children;
};

export default PublicRoute;
