import { api } from "./api";

export const getAllFilesAPI = async () => {
  try {
    const response = await api.get("/files");
    console.log("API Response:", JSON.stringify(response.data, null, 2));
    return response;
  } catch (error) {
    return error;
  }
};
