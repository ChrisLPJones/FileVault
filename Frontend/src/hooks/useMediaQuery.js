import { useSyncExternalStore } from "react";

// Phones and portrait tablets: the folder tree becomes a drawer and the details pane a bottom
// sheet. Keep in step with the breakpoint in FileManager/Mobile.scss.
export const NARROW_QUERY = "(max-width: 900px)";

// True while the CSS media query matches; updates when it changes (e.g. rotating a tablet)
export const useMediaQuery = (query) =>
    useSyncExternalStore(
        (onChange) => {
            const list = window.matchMedia(query);
            list.addEventListener("change", onChange);
            return () => list.removeEventListener("change", onChange);
        },
        () => window.matchMedia(query).matches
    );

export const useIsNarrow = () => useMediaQuery(NARROW_QUERY);
