import { api } from "./api";

export const deleteAPI = async (ids) => {
  // force ids into an array
  const arrayIds = Array.isArray(ids) ? ids : [ids];

  return api.delete("/delete", {
    data: { ids: arrayIds }
  });
};