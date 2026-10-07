import { createContext, useContext, useState } from "react";

const LayoutContext = createContext();

const validateLayout = (layout) => (["list", "grid"].includes(layout) ? layout : "grid");

export const LayoutProvider = ({ children, layout }) => {
  const [activeLayout, setActiveLayout] = useState(() => validateLayout(layout));

  return (
    <LayoutContext.Provider value={{ activeLayout, setActiveLayout }}>
      {children}
    </LayoutContext.Provider>
  );
};

export const useLayout = () => useContext(LayoutContext);
