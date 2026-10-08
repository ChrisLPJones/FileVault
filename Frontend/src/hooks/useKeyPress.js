import { useEffect, useMemo, useRef } from "react";

const normalizeKey = (key) => {
  return key.toLowerCase();
};

export const useKeyPress = (keys, callback, disable = false) => {
  const lastKeyPressed = useRef(new Set([]));
  const keysSet = useMemo(() => {
    return new Set(keys.map((key) => normalizeKey(key)));
  }, [keys]);

  const handleKeyDown = (e) => {
    if (e.repeat) return; // To prevent this function from triggering on key hold e.g. Ctrl hold
    // Typing in a field (e.g. the search box) keeps its normal keys: Delete, Home, Ctrl+A...
    if (e.target?.closest?.("input:not([type=checkbox]):not([type=radio]), textarea, select, [contenteditable='true']")) return;

    lastKeyPressed.current.add(normalizeKey(e.key));

    if (keysSet.isSubsetOf(lastKeyPressed.current) && !disable) {
      e.preventDefault();
      callback(e);
      return;
    }
  };

  const handleKeyUp = (e) => {
    lastKeyPressed.current.delete(normalizeKey(e.key));
  };

  const handleBlur = () => {
    lastKeyPressed.current.clear();
  };

  useEffect(() => {
    window.addEventListener("keydown", handleKeyDown);
    window.addEventListener("keyup", handleKeyUp);
    window.addEventListener("blur", handleBlur);

    return () => {
      window.removeEventListener("keydown", handleKeyDown);
      window.removeEventListener("keyup", handleKeyUp);
      window.removeEventListener("blur", handleBlur);
    };
  }, [keysSet, callback, disable]);
};
