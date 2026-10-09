import { useCallback, useEffect, useRef, useState } from "react";

// Which sections of the folder pane (Folders, Favourites, Recent) are expanded, remembered in
// this browser. Sections missing from storage default to open.
const STORAGE_KEY = "fv-quick-access";
const DEFAULTS = { folders: true, favourites: true, recent: true };

// The stored sections, or null when storage is unavailable or unreadable
const readStored = () => {
  try {
    const stored = JSON.parse(localStorage.getItem(STORAGE_KEY) || "{}");
    return stored && typeof stored === "object" ? stored : null;
  } catch {
    return null;
  }
};

// [expanded, toggle(key)]. Several components use this at once, so a toggle merges into what is
// stored; this copy of the state is the fallback when storage can't be read.
export const useNavSections = () => {
  const [expanded, setExpanded] = useState(() => ({ ...DEFAULTS, ...readStored() }));
  const latest = useRef(expanded);
  useEffect(() => {
    latest.current = expanded;
  }, [expanded]);

  // True when the last save failed: what is stored is then older than this copy
  const writeFailed = useRef(false);

  const toggle = useCallback((key) => {
    const current = writeFailed.current ? latest.current : { ...latest.current, ...readStored() };
    const next = { ...current, [key]: !current[key] };
    latest.current = next;
    setExpanded(next);
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
      writeFailed.current = false;
    } catch {
      // Storage unavailable or full: still works for this visit
      writeFailed.current = true;
    }
  }, []);

  return [expanded, toggle];
};
