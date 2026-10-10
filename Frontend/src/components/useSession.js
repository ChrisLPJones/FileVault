import { useCallback, useEffect, useState } from "react";
import { ensureSession } from "../api/api";
import { getToken, hasSessionHint, isTokenExpiring } from "../utils/auth";

// Server-confirmed session state for the route guards:
// "pending" | "authenticated" | "anonymous" | "unreachable". Starts settled when no request is
// needed (a live token, or no session hint), so those cases don't flash a loader.
export const useSession = () => {
    const [status, setStatus] = useState(() => {
        const token = getToken();
        if (token) return isTokenExpiring(token) ? "pending" : "authenticated";
        return hasSessionHint() ? "pending" : "anonymous";
    });
    const [attempt, setAttempt] = useState(0);

    useEffect(() => {
        let cancelled = false;
        ensureSession().then(
            (result) => !cancelled && setStatus(result),
            () => !cancelled && setStatus("unreachable")
        );
        return () => {
            cancelled = true;
        };
    }, [attempt]);

    const retry = useCallback(() => {
        setStatus("pending");
        setAttempt((n) => n + 1);
    }, []);

    return { status, retry };
};
