import { Navigate } from "react-router-dom";

const PublicRoute = ({ children }) => {
    const isAuthenticated = !!localStorage.getItem("token");

    if (isAuthenticated) {
        return <Navigate to="/dashboard" replace />;
    }

    return children;
};

export default PublicRoute;
