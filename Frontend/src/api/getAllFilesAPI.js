import { api } from "./api";

export const getAllFilesAPI = async () => {
  return api.get("/files");
};
