import { useEffect, useRef, useState } from "react";
import { MdOutlineFileUpload } from "react-icons/md";
import { useFileNavigation } from "../../contexts/FileNavigationContext";
import { useFiles } from "../../contexts/FilesContext";
import { useRecycleBin } from "../../contexts/RecycleBinContext";
import { useSearch } from "../../contexts/SearchContext";
import { useTranslation } from "../../contexts/TranslationProvider";
import { readDroppedItems } from "../../utils/folderUpload";
import { isOsFileDrag } from "./isOsFileDrag";
import "./UploadDropZone.scss";

// Files and folders dropped from the computer anywhere over the file manager go to the upload
// dialog's queue, into the folder that is open. A full-area overlay shows while they are dragged
// over. It listens on the element it is rendered in. There is no drop zone where uploading isn't
// possible (no upload permission, the recycle bin, search results) or while a dialog is open
// (the upload dialog has its own drop zone). Rows being dragged inside the app are ignored.
// `droppedRef` hands the dropped items to the upload dialog, which takes them when it opens.
export default function UploadDropZone({ permissions, triggerAction, droppedRef }) {
  const ref = useRef(null);
  const [active, setActive] = useState(false);
  const { currentFolder } = useFileNavigation();
  const { onError } = useFiles();
  const { isBinOpen } = useRecycleBin();
  const { term } = useSearch();
  const t = useTranslation();

  const enabled = !!permissions.upload && !isBinOpen && !term && !triggerAction.isActive;

  // The handlers always see the latest props without being re-attached
  const latest = useRef();
  useEffect(() => {
    latest.current = { triggerAction, droppedRef, onError };
  });

  // A file dropped where nothing takes it (the viewer, a dialog, the bin) must not make the browser
  // open it and leave the app. Only drags of files from the computer are touched.
  useEffect(() => {
    const stop = (e) => {
      if (isOsFileDrag(e)) e.preventDefault();
    };
    window.addEventListener("dragover", stop);
    window.addEventListener("drop", stop);
    return () => {
      window.removeEventListener("dragover", stop);
      window.removeEventListener("drop", stop);
    };
  }, []);

  useEffect(() => {
    const host = ref.current?.parentElement;
    if (!host || !enabled) return undefined;
    // dragenter and dragleave fire for every child element crossed, so count them
    let depth = 0;
    const reset = () => {
      depth = 0;
      setActive(false);
    };

    const onEnter = (e) => {
      if (!isOsFileDrag(e)) return;
      e.preventDefault();
      depth += 1;
      setActive(true);
    };
    const onOver = (e) => {
      if (!isOsFileDrag(e)) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = "copy";
    };
    const onLeave = (e) => {
      if (!isOsFileDrag(e)) return;
      depth = Math.max(0, depth - 1);
      if (depth === 0) setActive(false);
    };
    const onDrop = (e) => {
      if (!isOsFileDrag(e)) return;
      e.preventDefault();
      reset();
      const { triggerAction: trigger, droppedRef: dropped, onError: report } = latest.current;
      readDroppedItems(e.dataTransfer)
        .then((items) => {
          if (items.length === 0) return;
          dropped.current = items;
          trigger.show("uploadFile");
        })
        .catch((err) => report({ type: "upload", message: "Could not read the dropped folder" }, err));
    };

    host.addEventListener("dragenter", onEnter);
    host.addEventListener("dragover", onOver);
    host.addEventListener("dragleave", onLeave);
    host.addEventListener("drop", onDrop);
    // A drag that ends somewhere else (Esc, dropped outside the window) must not leave the overlay up
    window.addEventListener("dragend", reset);
    window.addEventListener("drop", reset);
    return () => {
      host.removeEventListener("dragenter", onEnter);
      host.removeEventListener("dragover", onOver);
      host.removeEventListener("dragleave", onLeave);
      host.removeEventListener("drop", onDrop);
      window.removeEventListener("dragend", reset);
      window.removeEventListener("drop", reset);
      setActive(false);
    };
  }, [enabled]);

  return (
    <div ref={ref} className="fm-drop-anchor">
      {active && enabled && (
        <div className="fm-drop-overlay" role="status">
          <MdOutlineFileUpload size={44} />
          <span>Drop to upload to {currentFolder?.name ?? t("home")}</span>
        </div>
      )}
    </div>
  );
}
