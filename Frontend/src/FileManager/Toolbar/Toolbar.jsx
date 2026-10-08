import { useState } from "react";
import { BsCopy, BsFolderPlus, BsGridFill, BsScissors } from "react-icons/bs";
import { FiRefreshCw } from "react-icons/fi";
import {
  MdOutlineDelete,
  MdOutlineFileDownload,
  MdOutlineFileUpload,
} from "react-icons/md";
import { BiRename } from "react-icons/bi";
import { FaListUl, FaRegPaste } from "react-icons/fa6";
import LayoutToggler from "./LayoutToggler";
import ToolbarOverflow from "./ToolbarOverflow";
import { useFileNavigation } from "../../contexts/FileNavigationContext";
import { useSelection } from "../../contexts/SelectionContext";
import { useClipBoard } from "../../contexts/ClipboardContext";
import { useLayout } from "../../contexts/LayoutContext";
import { validateApiCallback } from "../../utils/validateApiCallback";
import { useTranslation } from "../../contexts/TranslationProvider";
import "./Toolbar.scss";

const Toolbar = ({ onLayoutChange, onRefresh, triggerAction, permissions }) => {
  const [showToggleViewMenu, setShowToggleViewMenu] = useState(false);
  const { currentFolder } = useFileNavigation();
  const { selectedFiles, setSelectedFiles, handleDownload } = useSelection();
  const { clipBoard, setClipBoard, handleCutCopy, handlePasting } = useClipBoard();
  const { activeLayout, setActiveLayout } = useLayout();
  const t = useTranslation();

  // Toolbar Items
  const toolbarLeftItems = [
    {
      icon: <BsFolderPlus size={17} strokeWidth={0.3} />,
      text: t("newFolder"),
      permission: permissions.create,
      onClick: () => triggerAction.show("createFolder"),
    },
    {
      icon: <MdOutlineFileUpload size={18} />,
      text: t("upload"),
      permission: permissions.upload,
      onClick: () => triggerAction.show("uploadFile"),
    },
    {
      icon: <FaRegPaste size={18} />,
      text: t("paste"),
      permission: !!clipBoard,
      onClick: handleFilePasting,
      secondary: true,
    },
  ];

  const toolbarRightItems = [
    {
      icon: activeLayout === "grid" ? <BsGridFill size={19} /> : <FaListUl size={19} />,
      title: t("changeView"),
      onClick: () => setShowToggleViewMenu((prev) => !prev),
    },
    {
      icon: <FiRefreshCw size={19} />,
      title: t("refresh"),
      onClick: () => {
        validateApiCallback(onRefresh, "onRefresh");
        setClipBoard(null);
      },
    },
  ];

  function handleFilePasting() {
    handlePasting(currentFolder);
  }

  const handleDownloadItems = () => {
    handleDownload();
    setSelectedFiles([]);
  };

  // Narrow screens: the less-used actions (marked "secondary-action") move into a "⋯" menu
  const selecting = selectedFiles.length > 0;
  const changeLayout = (layout) => {
    setActiveLayout(layout);
    onLayoutChange(layout);
  };
  const overflowItems = [
    selecting && permissions.move && { text: t("cut"), icon: <BsScissors size={17} />, onClick: () => handleCutCopy(true) },
    selecting && permissions.copy && { text: t("copy"), icon: <BsCopy size={16} />, onClick: () => handleCutCopy(false) },
    clipBoard?.files?.length > 0 && { text: t("paste"), icon: <FaRegPaste size={17} />, onClick: handleFilePasting },
    selecting && selectedFiles.length === 1 && permissions.rename &&
      { text: t("rename"), icon: <BiRename size={18} />, onClick: () => triggerAction.show("rename") },
    { text: t("grid"), icon: <BsGridFill size={16} />, onClick: () => changeLayout("grid"), checked: activeLayout === "grid", divider: true },
    { text: t("list"), icon: <FaListUl size={16} />, onClick: () => changeLayout("list"), checked: activeLayout === "list" },
    { text: t("refresh"), icon: <FiRefreshCw size={16} />, onClick: toolbarRightItems[1].onClick, divider: true },
  ].filter(Boolean);
  // The first item after the file actions starts a new group
  overflowItems[0] = { ...overflowItems[0], divider: false };

  // Details, view and refresh: on the right in both toolbars
  const rightGroup = (
    <div>
      <ToolbarOverflow items={overflowItems} />
      {toolbarRightItems.map((item, index) => (
        <div key={index} className="toolbar-left-items">
          <button
            className="item-action icon-only secondary-action"
            title={item.title}
            onClick={item.onClick}
          >
            {item.icon}
          </button>
        </div>
      ))}

      {showToggleViewMenu && (
        <LayoutToggler
          setShowToggleViewMenu={setShowToggleViewMenu}
          onLayoutChange={onLayoutChange}
        />
      )}
    </div>
  );

  // Selected File/Folder Actions
  if (selectedFiles.length > 0) {
    return (
      <div className="toolbar file-selected">
        <div className="fm-toolbar file-action-container">
          <div>
            {permissions.move && (
              <button className="item-action file-action secondary-action" title={t("cut")} onClick={() => handleCutCopy(true)}>
                <BsScissors size={18} />
                <span>{t("cut")}</span>
              </button>
            )}
            {permissions.copy && (
              <button className="item-action file-action secondary-action" title={t("copy")} onClick={() => handleCutCopy(false)}>
                <BsCopy strokeWidth={0.1} size={17} />
                <span>{t("copy")}</span>
              </button>
            )}
            {clipBoard?.files?.length > 0 && (
              <button
                className="item-action file-action secondary-action"
                title={t("paste")}
                onClick={handleFilePasting}
                // disabled={!clipBoard}
              >
                <FaRegPaste size={18} />
                <span>{t("paste")}</span>
              </button>
            )}
            {selectedFiles.length === 1 && permissions.rename && (
              <button
                className="item-action file-action secondary-action"
                title={t("rename")}
                onClick={() => triggerAction.show("rename")}
              >
                <BiRename size={19} />
                <span>{t("rename")}</span>
              </button>
            )}
            {permissions.download && (
              <button className="item-action file-action" title={t("download")} onClick={handleDownloadItems}>
                <MdOutlineFileDownload size={19} />
                <span>{t("download")}</span>
              </button>
            )}
            {permissions.delete && (
              <button
                className="item-action file-action"
                title={t("delete")}
                onClick={() => triggerAction.show("delete")}
              >
                <MdOutlineDelete size={19} />
                <span>{t("delete")}</span>
              </button>
            )}
          </div>
          {rightGroup}
        </div>
      </div>
    );
  }
  //

  return (
    <div className="toolbar">
      <div className="fm-toolbar">
        <div>
          {toolbarLeftItems
            .filter((item) => item.permission)
            .map((item, index) => (
              <button className={`item-action ${item.secondary ? "secondary-action" : ""}`} key={index} title={item.text} onClick={item.onClick}>
                {item.icon}
                <span>{item.text}</span>
              </button>
            ))}
        </div>
        {rightGroup}
      </div>
    </div>
  );
};

Toolbar.displayName = "Toolbar";

export default Toolbar;
