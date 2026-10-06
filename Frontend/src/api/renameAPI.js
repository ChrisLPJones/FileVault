import { api } from "./api";

export const renameAPI = async (id, newName) => {
  return api.patch("/rename", { id, newName });
};
