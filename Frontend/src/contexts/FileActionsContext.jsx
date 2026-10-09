import { createContext, useContext } from "react";

// Actions on items that the page showing the file manager (Dashboard) carries out, because it
// owns the file list. (Opening a file is reported through FileManager's onFileOpen prop.)
//   setFavourite(items, favourite)  star (true) or unstar (false) files and folders
const FileActionsContext = createContext({
    setFavourite: () => {},
});

export const FileActionsProvider = FileActionsContext.Provider;

export const useFileActions = () => useContext(FileActionsContext);
