import React, { useEffect, useState } from "react";
import Collapse from "../../components/Collapse/Collapse";
import FileTypeIcon from "../../components/FileTypeIcon/FileTypeIcon";
import { MdKeyboardArrowRight } from "react-icons/md";
import { useFileNavigation } from "../../contexts/FileNavigationContext";
import { useSearch } from "../../contexts/SearchContext";
import { useRecycleBin } from "../../contexts/RecycleBinContext";

const FolderTree = ({ folder, onFileOpen }) => {
  const [isOpen, setIsOpen] = useState(false);
  const [isActive, setIsActive] = useState(false);
  const { currentPath, setCurrentPath, onFolderChange } = useFileNavigation();
  const { term, clearSearch } = useSearch();
  const { isBinOpen, closeBin } = useRecycleBin();
  // The recycle bin and search results aren't in any folder, so none is highlighted then
  const showActive = isActive && !isBinOpen && !term;

  const handleFolderSwitch = () => {
    clearSearch(); // opening a folder from the tree ends a search
    closeBin(); // and leaves the recycle bin
    setIsActive(true);
    onFileOpen(folder);
    setCurrentPath(folder.path);
    onFolderChange?.(folder.path);
  };

  const handleCollapseChange = (e) => {
    e.stopPropagation();
    setIsOpen((prev) => !prev);
  };

  useEffect(() => {
    setIsActive(currentPath === folder.path); //Setting isActive to a folder if its path matches currentPath

    // Open this folder when the current folder is anywhere inside it (a deep link or a search
    // result can open a folder several levels down)
    if (folder.path && currentPath.startsWith(`${folder.path}/`)) {
      setIsOpen(true);
    }
  }, [currentPath]);

  if (folder.subDirectories.length > 0) {
    return (
      <>
        <div
          className={`sb-folders-list-item ${showActive ? "active-list-item" : ""}`}
          onClick={handleFolderSwitch}
          title={folder.name}
        >
          <span onClick={handleCollapseChange}>
            <MdKeyboardArrowRight
              size={20}
              className={`folder-icon-default ${isOpen ? "folder-rotate-down" : ""}`}
            />
          </span>
          <div className="sb-folder-details">
            <FileTypeIcon name={folder.name} path={folder.path} isDirectory open={isOpen || showActive} size={22} />
            <span className="sb-folder-name" title={folder.name}>
              {folder.name}
            </span>
          </div>
        </div>
        <Collapse open={isOpen}>
          <div className="folder-collapsible">
            {folder.subDirectories.map((item, index) => (
              <FolderTree key={index} folder={item} onFileOpen={onFileOpen} />
            ))}
          </div>
        </Collapse>
      </>
    );
  } else {
    return (
      <div
        className={`sb-folders-list-item ${showActive ? "active-list-item" : ""}`}
        onClick={handleFolderSwitch}
        title={folder.name}
      >
        <span className="non-expanable"></span>
        <div className="sb-folder-details">
          <FileTypeIcon name={folder.name} path={folder.path} isDirectory open={showActive} size={22} />
          <span className="sb-folder-name" title={folder.name}>
            {folder.name}
          </span>
        </div>
      </div>
    );
  }
};

export default FolderTree;
