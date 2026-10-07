import axios from "axios";
import { useEffect, useState } from "react";
import { API_BASE_URL } from "../api/api";

const POLL_INTERVAL_MS = 10_000;

// Returns an error message if the API or its database is unreachable, otherwise null
const checkServer = async () => {
    try {
        await axios.get(`${API_BASE_URL}/ping`);
    } catch {
        return "Can't reach the FileVault server. Please try again shortly.";
    }

    try {
        await axios.get(`${API_BASE_URL}/pingsql`);
    } catch {
        return "The FileVault server can't reach its database. Please try again shortly.";
    }

    return null;
};

// Banner shown on the login/register pages while the backend is down
export default function ServerStatus() {
    const [errorMessage, setErrorMessage] = useState(null);

    useEffect(() => {
        let cancelled = false;
        const update = () => checkServer().then((message) => !cancelled && setErrorMessage(message));

        update();
        const interval = setInterval(update, POLL_INTERVAL_MS);

        return () => {
            cancelled = true;
            clearInterval(interval);
        };
    }, []);

    if (!errorMessage) return null;

    return (
        <div className="alert danger server-alert" role="alert">
            {errorMessage}
        </div>
    );
}
