import { api } from "./api";

export const createFolderAPI = async (name, parentId) => {
  return api.post("/folder", { name, parentId });
};
