import { useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { jwtDecode } from "jwt-decode";

const useAuthCheck = () => {
    const navigate = useNavigate();

    useEffect(() => {
        const token = localStorage.getItem("token");

        if (!token) {
            navigate("/login");
            return;
        }

        try {
            const decoded = jwtDecode(token);
            const expiry = decoded.exp * 1000;

            if (Date.now() > expiry) {
                localStorage.removeItem("token");
                navigate("/login");
                return;
            }
        } catch (err) {
            localStorage.removeItem("token");
            navigate("/login");
            return;
        }
    }, [navigate]);
};

export default useAuthCheck;
