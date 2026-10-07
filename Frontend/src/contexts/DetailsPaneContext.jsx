import { createContext, useCallback, useContext, useState } from "react";

// Whether the details pane (name, size, dates and a preview of the selection) is showing
// on the right of the file list. The choice is remembered in this browser.
const STORAGE_KEY = "fv-details-pane";

const readSaved = () => {
  try {
    return localStorage.getItem(STORAGE_KEY) === "open";
  } catch {
    return false;
  }
};

const DetailsPaneContext = createContext({
  isDetailsOpen: false,
  setDetailsOpen: () => {},
});

export const DetailsPaneProvider = ({ children }) => {
  const [isDetailsOpen, setOpenState] = useState(readSaved);

  const setDetailsOpen = useCallback((open) => {
    setOpenState((prev) => {
      const next = typeof open === "function" ? open(prev) : open;
      try {
        localStorage.setItem(STORAGE_KEY, next ? "open" : "closed");
      } catch {
        // Storage unavailable (private window etc.); the pane still works for this visit
      }
      return next;
    });
  }, []);

  return (
    <DetailsPaneContext.Provider value={{ isDetailsOpen, setDetailsOpen }}>
      {children}
    </DetailsPaneContext.Provider>
  );
};

export const useDetailsPane = () => useContext(DetailsPaneContext);
