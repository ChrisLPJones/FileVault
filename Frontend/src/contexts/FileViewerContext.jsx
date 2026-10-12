import { createContext, useCallback, useContext, useMemo, useState } from "react";

const FileViewerContext = createContext({ viewing: null, openViewer: () => {}, closeViewer: () => {} });

// The file shown full screen in the viewer (double-click, Enter or the context menu's Open on a
// file), or null while the viewer is closed
export const FileViewerProvider = ({ children }) => {
  const [viewing, setViewing] = useState(null);
  const openViewer = useCallback((file) => setViewing(file), []);
  const closeViewer = useCallback(() => setViewing(null), []);
  const value = useMemo(() => ({ viewing, openViewer, closeViewer }), [viewing, openViewer, closeViewer]);
  return <FileViewerContext.Provider value={value}>{children}</FileViewerContext.Provider>;
};

export const useFileViewer = () => useContext(FileViewerContext);
