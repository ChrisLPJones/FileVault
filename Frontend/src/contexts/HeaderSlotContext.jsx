import { createContext, useContext } from "react";

// The element in the middle of the top bar that pages can render controls into
// (the dashboard puts the file toolbar there). Null until the header has mounted.
export const HeaderSlotContext = createContext(null);

export const useHeaderSlot = () => useContext(HeaderSlotContext);
