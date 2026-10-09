import { createContext, useContext, useState } from "react";

const RecycleBinContext = createContext({ isBinOpen: false, openBin: () => {}, closeBin: () => {} });

// Whether the recycle bin is showing in place of the folder view
export const RecycleBinProvider = ({ children }) => {
  const [isBinOpen, setBinOpen] = useState(false);

  return (
    <RecycleBinContext.Provider
      value={{ isBinOpen, openBin: () => setBinOpen(true), closeBin: () => setBinOpen(false) }}
    >
      {children}
    </RecycleBinContext.Provider>
  );
};

export const useRecycleBin = () => useContext(RecycleBinContext);
