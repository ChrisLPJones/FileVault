import React, { useEffect, useState } from "react";
import FolderTree from "./FolderTree";
import { getParentPath } from "../../utils/getParentPath";
import { useFiles } from "../../contexts/FilesContext";
import { useTranslation } from "../../contexts/TranslationProvider";
import NavUser from "./NavUser";
import { MdLink, MdMenu, MdOutlineDelete } from "react-icons/md";
import { useNavigate } from "react-router-dom";
import { useRecycleBin } from "../../contexts/RecycleBinContext";
import { useSelection } from "../../contexts/SelectionContext";
import "./NavigationPane.scss";

const NavigationPane = ({ onFileOpen, compact = false, onToggleCompact }) => {
  const [foldersTree, setFoldersTree] = useState([]);
  const { files } = useFiles();
  const t = useTranslation();
  const { isBinOpen, openBin } = useRecycleBin();
  const { setSelectedFiles } = useSelection();
  const navigate = useNavigate();

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
      <button
        type="button"
        className={`sb-recycle-bin ${isBinOpen ? "active" : ""}`}
        title="Recycle bin"
        aria-pressed={isBinOpen}
        onClick={() => {
          setSelectedFiles([]); // the toolbar's actions don't apply in the bin
          openBin();
        }}
      >
        <MdOutlineDelete size={22} aria-hidden="true" />
        <span>Recycle bin</span>
      </button>
      {/* Its own page: every link shared, to copy, check the password of or revoke */}
      <button
        type="button"
        className="sb-recycle-bin sb-shared-links"
        title="Shared links"
        onClick={() => navigate("/shared-links")}
      >
        <MdLink size={22} aria-hidden="true" />
        <span>Shared links</span>
      </button>
      <NavUser compact={compact} />
    </div>
  );
};

NavigationPane.displayName = "NavigationPane";

export default NavigationPane;
