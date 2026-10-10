import { Navigate } from "react-router-dom";
import { useAuthToken } from "../utils/auth";
import { useSession } from "./useSession";
import "./SessionGate.css";

// Only render children once the server has confirmed the session (the access token only ever
// comes from a server response, so a hand-edited browser storage can't unlock the page)
const ProtectedRoute = ({ children }) => {
    const token = useAuthToken();
    const { status, retry } = useSession();

    if (status === "pending") {
        return (
            <div className="session-gate" role="status" aria-label="Loading">
                <div className="session-gate-spinner" />
            </div>
        );
    }

    if (status === "unreachable") {
        return (
            <div className="session-gate">
                <p className="session-gate-message" role="alert">Can&apos;t reach the server.</p>
                <button type="button" className="session-gate-retry" onClick={retry}>Retry</button>
            </div>
        );
    }

    // A token lost later (signed out in another tab, session rejected) also sends the user away
    if (status === "anonymous" || !token) {
        return <Navigate to="/login" replace />;
    }

    return children;
};

export default ProtectedRoute;
