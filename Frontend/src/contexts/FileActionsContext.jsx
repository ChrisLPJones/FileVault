import { createContext, useContext } from "react";

// Actions on items that the page showing the file manager (Dashboard) carries out, because it
// owns the file list: starring items and recording that a file was opened.
//   setFavourite(items, favourite)  star (true) or unstar (false) files and folders
//   markOpened(file)                the file was previewed or downloaded
const FileActionsContext = createContext({
    setFavourite: () => {},
    markOpened: () => {},
});

export const FileActionsProvider = FileActionsContext.Provider;

export const useFileActions = () => useContext(FileActionsContext);
