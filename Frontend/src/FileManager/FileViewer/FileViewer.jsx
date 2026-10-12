import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { MdChevronLeft, MdChevronRight, MdClose, MdOutlineFileDownload } from "react-icons/md";
import Loader from "../../components/Loader/Loader";
import { useFileNavigation } from "../../contexts/FileNavigationContext";
import { useFileViewer } from "../../contexts/FileViewerContext";
import { formatBytes } from "../../utils/formatBytes";
import { AUTO_PREVIEW_LIMIT, previewKind } from "../preview/previewKind";
import { useFileContent } from "../preview/useFileContent";
import { mediaProblemText, useMediaPlayback } from "../preview/useMediaPlayback";
import "./FileViewer.scss";

const FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select, textarea, video[controls], audio[controls], iframe, [tabindex]:not([tabindex="-1"])';

// What the file looks like. Rendered with key={file._id}, so each file starts fresh and moving to
// another file cancels the request for the one before.
function ViewerBody({ file, onDownload }) {
  const kind = previewKind(file.name);
  const [requested, setRequested] = useState(false);
  const shouldLoad = !!kind && (requested || file.size <= AUTO_PREVIEW_LIMIT);
  const state = useFileContent(file._id, kind, shouldLoad, file.name);
  const media = useMediaPlayback(kind);

  if (!kind) {
    return (
      <div className="fv-message">
        <p>No preview for this file type</p>
        {onDownload && <button type="button" className="fv-button" onClick={onDownload}>Download file</button>}
      </div>
    );
  }

  if (!shouldLoad) {
    return (
      <div className="fv-message">
        <button type="button" className="fv-button" onClick={(e) => {
          // The button goes away, and focus with it: keep it in the viewer so Esc and the arrows still work
          e.currentTarget.closest('[role="dialog"]')?.focus();
          setRequested(true);
        }}>
          Show preview ({formatBytes(file.size)})
        </button>
      </div>
    );
  }

  if (state.error) {
    return (
      <div className="fv-message" role="alert">
        <p>Couldn&apos;t load this file. It may have been deleted, or the connection failed.</p>
      </div>
    );
  }

  if (state.loading) {
    return (
      <div className="fv-message">
        <Loader loading />
      </div>
    );
  }

  if (media.status === "error") {
    return (
      <div className="fv-message" role="alert">
        <p>{mediaProblemText(kind, media.status)}</p>
        {onDownload && <button type="button" className="fv-button" onClick={onDownload}>Download file</button>}
      </div>
    );
  }

  return (
    <div className={`fv-content fv-${kind}`}>
      {/* An image is only ever shown through <img>, which doesn't run an SVG's scripts */}
      {kind === "image" && state.url && <img src={state.url} alt={file.name} />}
      {kind === "video" && state.url && <video src={state.url} controls {...media.props} />}
      {kind === "audio" && state.url && <audio src={state.url} controls {...media.props} />}
      {media.status === "no-picture" && (
        <p className="fv-note">
          {mediaProblemText(kind, media.status)}
          {onDownload && <> <button type="button" className="fv-link" onClick={onDownload}>Download it</button></>}
        </p>
      )}
      {kind === "pdf" && state.url && <iframe src={state.url} title={file.name} />}
      {/* Text is a React text child, never HTML */}
      {kind === "text" && state.text !== null && (
        <>
          <pre>{state.text}</pre>
          {state.truncated && <p className="fv-note">Showing the start of the file only. Download it to see all of it.</p>}
        </>
      )}
    </div>
  );
}

// Full-screen viewer for the file that was opened. Previous/next move through the files (not
// folders) of the open folder in the list's current order.
export default function FileViewer({ onDownload, canDownload = true }) {
  const { viewing, openViewer, closeViewer } = useFileViewer();
  const { currentPathFiles } = useFileNavigation();
  const dialogRef = useRef(null);
  const returnFocusRef = useRef(null);
  const isOpen = !!viewing;
  const lastNameRef = useRef("");
  useEffect(() => {
    if (viewing) lastNameRef.current = viewing.name;
  }, [viewing]);

  const siblings = currentPathFiles.filter((f) => !f.isDirectory);
  // The file as the list has it now (renamed, say), falling back to the one that was opened
  const current = isOpen ? siblings.find((f) => f._id === viewing._id) ?? viewing : null;
  const index = current ? siblings.findIndex((f) => f._id === current._id) : -1;

  // Remember what had focus, move focus in, and give it back on close
  useEffect(() => {
    if (!isOpen) return undefined;
    returnFocusRef.current = document.activeElement;
    // The double-click that opened the viewer can leave text selected
    window.getSelection()?.removeAllRanges();
    // The dialog itself, not a button: the Enter that opened it must not also press one
    dialogRef.current?.focus();
    return () => {
      const el = returnFocusRef.current;
      if (el?.isConnected) el.focus();
      else document.querySelector(`.file-item-container[title="${CSS.escape(lastNameRef.current)}"]`)?.focus();
    };
  }, [isOpen]);

  const go = (step) => {
    const next = siblings[index + step];
    if (next) openViewer(next);
  };

  const handleKeyDown = (e) => {
    if (e.key === "Escape") {
      e.stopPropagation();
      closeViewer();
    } else if ((e.key === "ArrowLeft" || e.key === "ArrowRight") && !["VIDEO", "AUDIO", "INPUT"].includes(e.target.tagName)) {
      e.preventDefault();
      go(e.key === "ArrowLeft" ? -1 : 1);
    } else if (e.key === "Tab") {
      // Keep focus inside the viewer
      const items = [...dialogRef.current.querySelectorAll(FOCUSABLE)];
      if (items.length === 0) return;
      const first = items[0];
      const last = items[items.length - 1];
      if (e.shiftKey && (document.activeElement === first || document.activeElement === dialogRef.current)) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      } else if (!dialogRef.current.contains(document.activeElement)) {
        e.preventDefault();
        first.focus();
      }
    }
  };

  if (!isOpen || !current) return null;

  const download = canDownload && onDownload ? () => onDownload([current]) : null;

  return createPortal(
    <div
      ref={dialogRef}
      className="fv-overlay"
      role="dialog"
      aria-modal="true"
      aria-label={`Viewing ${current.name}`}
      tabIndex={-1}
      onKeyDown={handleKeyDown}
    >
      <header className="fv-header">
        <div className="fv-title">
          <span className="fv-name" title={current.name}>{current.name}</span>
          {current.size > 0 && <span className="fv-size">{formatBytes(current.size)}</span>}
        </div>
        {download && (
          <button type="button" className="fv-icon-button" aria-label="Download" title="Download" onClick={download}>
            <MdOutlineFileDownload size={22} />
          </button>
        )}
        <button type="button" className="fv-icon-button fv-close" aria-label="Close" title="Close (Esc)" onClick={closeViewer}>
          <MdClose size={22} />
        </button>
      </header>
      <div className="fv-stage">
        <button type="button" className="fv-nav fv-prev" aria-label="Previous file" onClick={() => go(-1)} disabled={index <= 0}>
          <MdChevronLeft size={30} />
        </button>
        <ViewerBody key={current._id} file={current} onDownload={download} />
        <button type="button" className="fv-nav fv-next" aria-label="Next file" onClick={() => go(1)} disabled={index >= siblings.length - 1}>
          <MdChevronRight size={30} />
        </button>
      </div>
    </div>,
    document.body
  );
}
