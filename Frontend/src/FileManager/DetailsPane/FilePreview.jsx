import { useEffect, useState } from "react";
import { fetchFileBlob } from "../../api/downloadFileAPI";
import { getFileExtension } from "../../utils/getFileExtension";
import { formatBytes } from "../../utils/formatBytes";
import Loader from "../../components/Loader/Loader";

const IMAGE = ["jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "avif"];
const VIDEO = ["mp4", "webm", "mov", "m4v"];
const AUDIO = ["mp3", "wav", "m4a", "ogg", "aac", "flac"];
const PDF = ["pdf"];
const TEXT = ["txt", "md", "log", "csv", "json", "xml", "yml", "yaml", "ini", "cfg", "conf",
  "js", "jsx", "ts", "tsx", "css", "scss", "html", "htm", "py", "java", "c", "h", "cpp", "cs",
  "php", "rb", "go", "rs", "sql", "sh", "ps1", "bat"];

// Bigger files are only downloaded for a preview when asked, since the whole file is fetched
const AUTO_PREVIEW_LIMIT = 25 * 1024 * 1024;
// Only the start of long text files is shown
const TEXT_PREVIEW_CHARS = 20000;

const previewKind = (name) => {
  const ext = getFileExtension(name)?.toLowerCase();
  if (IMAGE.includes(ext)) return "image";
  if (VIDEO.includes(ext)) return "video";
  if (AUDIO.includes(ext)) return "audio";
  if (PDF.includes(ext)) return "pdf";
  if (TEXT.includes(ext)) return "text";
  return null;
};

// The preview at the bottom of the details pane: images, video, audio, PDFs and text.
// Rendered with key={file._id}, so its state starts fresh for each file.
export default function FilePreview({ file, customPreview }) {
  const kind = previewKind(file.name);
  const canPreview = !!kind && !customPreview;
  const [requested, setRequested] = useState(false);
  const shouldLoad = canPreview && (requested || file.size <= AUTO_PREVIEW_LIMIT);
  const [state, setState] = useState({ loading: shouldLoad, error: false, url: null, text: null });

  useEffect(() => {
    if (!shouldLoad) return undefined;
    let url = null;
    let cancelled = false;

    fetchFileBlob(file._id)
      .then(async (blob) => {
        if (cancelled) return;
        if (kind === "text") {
          const text = await blob.slice(0, TEXT_PREVIEW_CHARS * 4).text();
          if (!cancelled) setState({ loading: false, error: false, url: null, text: text.slice(0, TEXT_PREVIEW_CHARS) });
        } else {
          url = URL.createObjectURL(blob);
          setState({ loading: false, error: false, url, text: null });
        }
      })
      .catch((err) => {
        console.error(err);
        if (!cancelled) setState({ loading: false, error: true, url: null, text: null });
      });

    // Release the object URL created for this file
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [file._id, kind, shouldLoad]);

  if (customPreview) return <div className="details-preview">{customPreview}</div>;

  if (!kind) {
    return <div className="details-preview details-preview-empty">No preview available</div>;
  }

  if (!shouldLoad) {
    return (
      <div className="details-preview details-preview-empty">
        <button type="button" className="details-preview-load" onClick={() => {
            setRequested(true);
            setState((prev) => ({ ...prev, loading: true }));
          }}>
          Show preview ({formatBytes(file.size)})
        </button>
      </div>
    );
  }

  if (state.error) {
    return <div className="details-preview details-preview-empty">Couldn&apos;t load the preview</div>;
  }

  if (state.loading) {
    return (
      <div className="details-preview details-preview-empty">
        <Loader loading />
      </div>
    );
  }

  return (
    <div className={`details-preview details-preview-${kind}`}>
      {kind === "image" && state.url && <img src={state.url} alt={file.name} />}
      {kind === "video" && state.url && <video src={state.url} controls />}
      {kind === "audio" && state.url && <audio src={state.url} controls />}
      {kind === "pdf" && state.url && <iframe src={state.url} title={file.name} />}
      {kind === "text" && state.text !== null && <pre>{state.text}</pre>}
    </div>
  );
}
