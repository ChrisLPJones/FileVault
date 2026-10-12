import { useState } from "react";
import { formatBytes } from "../../utils/formatBytes";
import Loader from "../../components/Loader/Loader";
import { AUTO_PREVIEW_LIMIT, previewKind } from "../preview/previewKind";
import { useFileContent } from "../preview/useFileContent";
import { mediaProblemText, useMediaPlayback } from "../preview/useMediaPlayback";

// The preview at the bottom of the details pane: images, video, audio, PDFs and text.
// Rendered with key={file._id}, so its state starts fresh for each file.
export default function FilePreview({ file, customPreview }) {
  const kind = previewKind(file.name);
  const canPreview = !!kind && !customPreview;
  const [requested, setRequested] = useState(false);
  const shouldLoad = canPreview && (requested || file.size <= AUTO_PREVIEW_LIMIT);
  const state = useFileContent(file._id, kind, shouldLoad, file.name);
  const media = useMediaPlayback(kind);

  if (customPreview) return <div className="details-preview">{customPreview}</div>;

  if (!kind) {
    return <div className="details-preview details-preview-empty">No preview available</div>;
  }

  if (!shouldLoad) {
    return (
      <div className="details-preview details-preview-empty">
        <button type="button" className="details-preview-load" onClick={() => {
            setRequested(true);
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

  if (media.status === "error") {
    return <div className="details-preview details-preview-empty" role="alert">{mediaProblemText(kind, media.status)}</div>;
  }

  return (
    <div className={`details-preview details-preview-${kind}`}>
      {kind === "image" && state.url && <img src={state.url} alt={file.name} />}
      {kind === "video" && state.url && <video src={state.url} controls {...media.props} />}
      {kind === "audio" && state.url && <audio src={state.url} controls {...media.props} />}
      {media.status === "no-picture" && <p className="details-preview-note">{mediaProblemText(kind, media.status)}</p>}
      {kind === "pdf" && state.url && <iframe src={`${state.url}#toolbar=0&navpanes=0`} title={file.name} />}
      {kind === "text" && state.text !== null && <pre>{state.text}</pre>}
    </div>
  );
}
