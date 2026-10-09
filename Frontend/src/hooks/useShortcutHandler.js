import { useKeyPress } from "./useKeyPress";
import { shortcuts } from "../utils/shortcuts";
import { useClipBoard } from "../contexts/ClipboardContext";
import { useFileNavigation } from "../contexts/FileNavigationContext";
import { useSelection } from "../contexts/SelectionContext";
import { useLayout } from "../contexts/LayoutContext";
import { validateApiCallback } from "../utils/validateApiCallback";
import { useRecycleBin } from "../contexts/RecycleBinContext";

export const useShortcutHandler = (triggerAction, onRefresh, permissions) => {
  const { setClipBoard, handleCutCopy, handlePasting } = useClipBoard();
  const { currentFolder, currentPathFiles } = useFileNavigation();
  const { selectedFiles, setSelectedFiles, handleDownload } = useSelection();
  const { setActiveLayout } = useLayout();
  const { isBinOpen } = useRecycleBin();
  // Off while an action (dialog, rename) is open, and in the recycle bin, whose items aren't files to act on
  const disabled = triggerAction.isActive || isBinOpen;

  const triggerCreateFolder = () => {
    permissions.create && triggerAction.show("createFolder");
  };

  const triggerUploadFiles = () => {
    permissions.upload && triggerAction.show("uploadFile");
  };

  const triggerCutItems = () => {
    permissions.move && handleCutCopy(true);
  };

  const triggerCopyItems = () => {
    permissions.copy && handleCutCopy(false);
  };

  const triggerPasteItems = () => {
    handlePasting(currentFolder);
  };

  const triggerRename = () => {
    permissions.rename && triggerAction.show("rename");
  };

  const triggerDownload = () => {
    permissions.download && handleDownload();
  };

  const triggerDelete = () => {
    if (permissions.delete && selectedFiles.length) {
      triggerAction.show("delete");
    }
  };

  const triggerSelectFirst = () => {
    if (currentPathFiles.length > 0) {
      setSelectedFiles([currentPathFiles[0]]);
    }
  };

  const triggerSelectLast = () => {
    if (currentPathFiles.length > 0) {
      setSelectedFiles([currentPathFiles.at(-1)]);
    }
  };

  const triggerSelectAll = () => {
    setSelectedFiles(currentPathFiles);
  };

  const triggerClearSelection = () => {
    setSelectedFiles((prev) => (prev.length > 0 ? [] : prev));
  };

  const triggerRefresh = () => {
    validateApiCallback(onRefresh, "onRefresh");
    setClipBoard(null);
  };

  const triggerGridLayout = () => {
    setActiveLayout("grid");
  };
  const triggerListLayout = () => {
    setActiveLayout("list");
  };

  // Keypress detection will be disbaled when some Action is in active state.
  useKeyPress(shortcuts.createFolder, triggerCreateFolder, disabled);
  useKeyPress(shortcuts.uploadFiles, triggerUploadFiles, disabled);
  useKeyPress(shortcuts.cut, triggerCutItems, disabled);
  useKeyPress(shortcuts.copy, triggerCopyItems, disabled);
  useKeyPress(shortcuts.paste, triggerPasteItems, disabled);
  useKeyPress(shortcuts.rename, triggerRename, disabled);
  useKeyPress(shortcuts.download, triggerDownload, disabled);
  useKeyPress(shortcuts.delete, triggerDelete, disabled);
  useKeyPress(shortcuts.jumpToFirst, triggerSelectFirst, disabled);
  useKeyPress(shortcuts.jumpToLast, triggerSelectLast, disabled);
  useKeyPress(shortcuts.selectAll, triggerSelectAll, disabled);
  useKeyPress(shortcuts.clearSelection, triggerClearSelection, disabled);
  useKeyPress(shortcuts.refresh, triggerRefresh, disabled);
  useKeyPress(shortcuts.gridLayout, triggerGridLayout, disabled);
  useKeyPress(shortcuts.listLayout, triggerListLayout, disabled);
};
