import React, { useEffect, useState } from "react";
import FolderTree from "./FolderTree";
import { getParentPath } from "../../utils/getParentPath";
import { useFiles } from "../../contexts/FilesContext";
import { useTranslation } from "../../contexts/TranslationProvider";
import NavUser from "./NavUser";
import QuickAccess from "./QuickAccess";
import { MdMenu } from "react-icons/md";
import "./NavigationPane.scss";

const NavigationPane = ({ onFileOpen, compact = false, onToggleCompact }) => {
  const [foldersTree, setFoldersTree] = useState([]);
  const { files } = useFiles();
  const t = useTranslation();

  const createChildRecursive = (path, foldersStruct) => {
    if (!foldersStruct[path]) return []; // No children for this path (folder)

    return foldersStruct[path]?.map((folder) => {
      return {
        ...folder,
        subDirectories: createChildRecursive(folder.path, foldersStruct),
      };
    });
  };

  useEffect(() => {
    if (Array.isArray(files)) {
      const folders = files.filter((file) => file.isDirectory);
      // Grouping folders by parent path
      const foldersStruct = Object.groupBy(folders, ({ path }) => getParentPath(path));
      setFoldersTree(() => {
        const rootPath = "";
        return createChildRecursive(rootPath, foldersStruct);
      });
    }
  }, [files]);

  return (
    <div className={`sb-folders-list ${compact ? "compact" : ""}`}>
      <button
        type="button"
        className="nav-toggle"
        title={compact ? "Expand folders" : "Collapse folders"}
        onClick={onToggleCompact}
      >
        <MdMenu size={20} />
      </button>
      <div className="sb-folders-scroll">
        <QuickAccess compact={compact} onExpand={onToggleCompact} onFileOpen={onFileOpen} />
        {foldersTree?.length > 0 ? (
          <>
            {foldersTree?.map((folder, index) => {
              return <FolderTree key={index} folder={folder} onFileOpen={onFileOpen} />;
            })}
          </>
        ) : (
          <div className="empty-nav-pane">{t("nothingHereYet")}</div>
        )}
      </div>
      <NavUser compact={compact} />
    </div>
  );
};

NavigationPane.displayName = "NavigationPane";

export default NavigationPane;
