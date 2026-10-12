import { useEffect, useState } from "react";
import { fetchFileBlob } from "../../api/downloadFileAPI";
import { TEXT_PREVIEW_CHARS, previewMime } from "./previewKind";

// Loads a file for previewing: an object URL (image, video, audio, PDF) or its first characters
// (text). Starts again when the file changes and, when it changes or the component goes away,
// cancels a request still in flight and revokes the object URL. Callers that show one file for
// the component's whole life key it by file id, so the state starts fresh for each file.
// Returns { loading, error, url, text, truncated }.
export function useFileContent(fileId, kind, shouldLoad, name) {
  // Results are tagged with what they are for, so a result for an earlier file is never shown
  const key = `${fileId}|${kind}`;
  const [result, setResult] = useState({ key: null, error: false, url: null, text: null, truncated: false });

  useEffect(() => {
    if (!shouldLoad) return undefined;
    let url = null;
    let cancelled = false;
    const controller = new AbortController();

    fetchFileBlob(fileId, controller.signal)
      .then(async (blob) => {
        if (cancelled) return;
        if (kind === "text") {
          const text = await blob.slice(0, TEXT_PREVIEW_CHARS * 4).text();
          if (!cancelled) {
            setResult({
              key, error: false, url: null,
              text: text.slice(0, TEXT_PREVIEW_CHARS),
              truncated: text.length > TEXT_PREVIEW_CHARS || blob.size > TEXT_PREVIEW_CHARS * 4,
            });
          }
        } else {
          // Re-wrapped with a fixed type chosen by extension, not the one the server sent
          url = URL.createObjectURL(new Blob([blob], { type: previewMime(name) }));
          setResult({ key, error: false, url, text: null, truncated: false });
        }
      })
      .catch((err) => {
        if (cancelled) return;
        console.error(err);
        setResult({ key, error: true, url: null, text: null, truncated: false });
      });

    return () => {
      cancelled = true;
      controller.abort();
      if (url) URL.revokeObjectURL(url);
    };
  }, [fileId, kind, shouldLoad, key, name]);

  const ready = shouldLoad && result.key === key;
  return ready
    ? { loading: false, error: result.error, url: result.url, text: result.text, truncated: result.truncated }
    : { loading: shouldLoad, error: false, url: null, text: null, truncated: false };
}
