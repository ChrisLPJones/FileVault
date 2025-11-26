import { Navigate } from "react-router-dom";

// Simple wrapper for protected routes
const ProtectedRoute = ({ children }) => {
    const isAuthenticated = !!localStorage.getItem("token"); // or your auth logic

    if (!isAuthenticated) {
        // Redirect to login if not logged in
        return <Navigate to="/login" replace />;
    }

    return children;
};

export default ProtectedRoute;
