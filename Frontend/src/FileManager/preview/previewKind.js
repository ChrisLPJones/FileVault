import { getFileExtension } from "../../utils/getFileExtension";

const IMAGE = ["jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "avif"];
const VIDEO = ["mp4", "webm", "mov", "m4v"];
const AUDIO = ["mp3", "wav", "m4a", "ogg", "aac", "flac"];
const PDF = ["pdf"];
const TEXT = ["txt", "md", "log", "csv", "json", "xml", "yml", "yaml", "ini", "cfg", "conf",
  "js", "jsx", "ts", "tsx", "css", "scss", "html", "htm", "py", "java", "c", "h", "cpp", "cs",
  "php", "rb", "go", "rs", "sql", "sh", "ps1", "bat"];

// Bigger files are only downloaded for a preview when asked, since the whole file is fetched
export const AUTO_PREVIEW_LIMIT = 25 * 1024 * 1024;
// Only the start of long text files is shown
export const TEXT_PREVIEW_CHARS = 20000;

// The type a previewed file is given, by extension. The type the server sends is whatever the
// upload declared, so it is never trusted: a "PDF" stored as text/html must not run as HTML.
const MIME = {
  png: "image/png", jpg: "image/jpeg", jpeg: "image/jpeg", gif: "image/gif", webp: "image/webp",
  bmp: "image/bmp", avif: "image/avif", svg: "image/svg+xml",
  mp4: "video/mp4", m4v: "video/mp4", webm: "video/webm", mov: "video/quicktime",
  mp3: "audio/mpeg", wav: "audio/wav", m4a: "audio/mp4", ogg: "audio/ogg", aac: "audio/aac", flac: "audio/flac",
  pdf: "application/pdf",
};
export const previewMime = (name) => MIME[getFileExtension(name)?.toLowerCase()] ?? "application/octet-stream";

// "image" | "video" | "audio" | "pdf" | "text", or null when there is no preview for the type
export const previewKind = (name) => {
  const ext = getFileExtension(name)?.toLowerCase();
  if (IMAGE.includes(ext)) return "image";
  if (VIDEO.includes(ext)) return "video";
  if (AUDIO.includes(ext)) return "audio";
  if (PDF.includes(ext)) return "pdf";
  if (TEXT.includes(ext)) return "text";
  return null;
};
