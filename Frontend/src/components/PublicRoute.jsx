import { Navigate } from "react-router-dom";
import { useAuthToken } from "../utils/auth";
import { useSession } from "./useSession";
import "./SessionGate.css";

// Login/register pages: send logged-in users to the dashboard. The page shows at once for
// visitors with no session hint; otherwise it waits for the one refresh that settles it.
const PublicRoute = ({ children }) => {
    const token = useAuthToken();
    const { status } = useSession();

    if (status === "pending") {
        return (
            <div className="session-gate" role="status" aria-label="Loading">
                <div className="session-gate-spinner" />
            </div>
        );
    }

    if (status === "authenticated" && token) {
        return <Navigate to="/dashboard" replace />;
    }

    // Anonymous, or the server can't be reached (ServerStatus reports outages)
    return children;
};

export default PublicRoute;
