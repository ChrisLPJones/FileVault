import { createContext, useCallback, useContext, useMemo, useRef, useState } from "react";
import { getUsageAPI } from "../api/accountAPI";

// The account's storage usage ({ used, quota, maxUploadBytes, maxFileBytes }), shared by the folder
// pane's usage text and the upload dialog's space check. refreshUsage() loads it again (after an
// upload, delete or emptying the bin) and resolves to the new value, or null if it couldn't be loaded.
const UsageContext = createContext({ usage: null, refreshUsage: async () => null });

export const UsageProvider = ({ children }) => {
  const [usage, setUsage] = useState(null);
  const latestRequest = useRef(0);

  const refreshUsage = useCallback(async () => {
    const request = ++latestRequest.current;
    try {
      const data = await getUsageAPI();
      // A slower, older response must not replace a newer one
      if (request === latestRequest.current) setUsage(data);
      return data;
    } catch {
      return null;
    }
  }, []);

  const value = useMemo(() => ({ usage, refreshUsage }), [usage, refreshUsage]);
  return <UsageContext.Provider value={value}>{children}</UsageContext.Provider>;
};

export const useUsage = () => useContext(UsageContext);
