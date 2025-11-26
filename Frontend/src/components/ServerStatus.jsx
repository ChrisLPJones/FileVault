import { Alert } from "react-bootstrap";
import axios from "axios";
import { useEffect, useState } from "react";

const API_URL = import.meta.env.VITE_API_URL;

export default function ServerStatus() {
    const [serverUp, setServerUp] = useState(true);
    const [errorMessage, setErrorMessage] = useState();

    const checkServer = async () => {
        let serverIsUp = true;
        let error = null;

        try {
            await axios.get(`${API_URL}/ping`);
        } catch (err) {
            serverIsUp = false;
            error = "503 Service Unavailable";
            setServerUp(serverIsUp);
            setErrorMessage(error);
            return;
        }

        try {
            await axios.get(`${API_URL}/pingsql`);
        } catch (err) {
            serverIsUp = false;
            error = "500 Internal Server Error";
        }
        
        setServerUp(serverIsUp);
        setErrorMessage(error);

    };
    
    useEffect(() => {

        checkServer();

        const interval = setInterval(() => {
            checkServer();
        }, 3000);

        return () => clearInterval(interval);
        
    }, []);

    if (!serverUp) {
        return (
            <Alert className="server-alert" variant="danger">
                {errorMessage}
            </Alert>
        );
    }
    return null;



}
