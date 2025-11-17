import axios from "axios";

const API_URL = import.meta.env.VITE_API_URL;

export const login = async (Email, Password) => {
    const response = await axios.post(
        `${API_URL}/user/login`,
        { Email, Password },
        {
            // don’t throw, consider all HTTP responses as valid.
            validateStatus: () => true,
        }
    );

    return response;
};

export const register = async (Username, Email, Password) => {
	const response = await axios.post(
    `${API_URL}/user/register`,
        { Username, Email, Password },
        {
            // don’t throw, consider all HTTP responses as valid.
            validateStatus: () => true,
        }
	);
	return response
};
