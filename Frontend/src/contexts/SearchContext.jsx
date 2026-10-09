import { createContext, useContext, useEffect, useState } from "react";
import { useFileNavigation } from "./FileNavigationContext";
import { useSelection } from "./SelectionContext";

const SEARCH_DELAY_MS = 200;

const SearchContext = createContext();

// The search box text, the debounced term the results use, and the item to select once its
// folder has opened (after clicking a result)
export const SearchProvider = ({ children }) => {
  const [query, setQuery] = useState("");
  const [term, setTerm] = useState("");
  const [revealPath, setRevealPath] = useState(null);

  useEffect(() => {
    const timer = setTimeout(() => setTerm(query.trim()), query.trim() ? SEARCH_DELAY_MS : 0);
    return () => clearTimeout(timer);
  }, [query]);

  const clearSearch = () => {
    setQuery("");
    setTerm("");
  };

  // Show a search straight away, without the typing delay (restoring one from the address)
  const setSearchTerm = (value) => {
    setQuery(value);
    setTerm(value.trim());
  };

  return (
    <SearchContext.Provider value={{ query, setQuery, term, setSearchTerm, clearSearch, revealPath, setRevealPath }}>
      {children}
    </SearchContext.Provider>
  );
};

export const useSearch = () => useContext(SearchContext);

// Selects the item picked from the search results once its folder's files are showing.
// Opening a folder clears the selection first (see useFileList), so this waits for the new list.
export const SearchReveal = () => {
  const { revealPath, setRevealPath } = useSearch();
  const { currentPathFiles } = useFileNavigation();
  const { setSelectedFiles } = useSelection();

  useEffect(() => {
    if (!revealPath) return;
    const file = currentPathFiles.find((f) => f.path === revealPath);
    if (!file) return;

    // Next frame: after the folder change has finished clearing the old selection
    const frame = requestAnimationFrame(() => {
      setSelectedFiles([file]);
      setRevealPath(null);
      // Bring it into view (the list renders items with their name as the title)
      document.querySelector(`.file-item-container[title="${CSS.escape(file.name)}"]`)?.scrollIntoView({ block: "nearest" });
    });
    return () => cancelAnimationFrame(frame);
  }, [revealPath, currentPathFiles, setSelectedFiles, setRevealPath]);

  return null;
};
