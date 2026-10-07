import { Navigate } from "react-router-dom";
import { isAuthenticated } from "../utils/auth";

// Only render children for a logged-in user with an unexpired token
const ProtectedRoute = ({ children }) => {
    if (!isAuthenticated()) {
        return <Navigate to="/login" replace />;
    }

    return children;
};

export default ProtectedRoute;
