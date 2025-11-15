import axios from "axios";

const API_URL = "http://localhost:3000";

export const login = async (Email, Password) => {
    const response = await axios.post(
        `${API_URL}/user/login`,
        { Email, Password },
        {   // don’t throw, consider all HTTP responses as valid.
            validateStatus: () => true,
        }
    );

    return response.data;
};
