import { useId } from "react";
import "./FileTypeIcon.css";

// Desktop-style file and folder icons (Windows 11 / macOS Finder look), drawn as SVG.
// Folders: solid folder with a tab, a sheet of paper and a lighter front flap.
// Files: white page with a folded corner, a coloured type symbol and an extension label.

const TYPES = [
  { type: "pdf", color: "#e5252a", glyph: "lines", exts: ["pdf"] },
  { type: "word", color: "#2b6cd4", glyph: "lines", exts: ["doc", "docx", "odt", "rtf", "pages"] },
  { type: "sheet", color: "#1f8f4e", glyph: "grid", exts: ["xls", "xlsx", "xlsm", "csv", "ods", "numbers"] },
  { type: "slides", color: "#d9541e", glyph: "slides", exts: ["ppt", "pptx", "odp", "key"] },
  { type: "image", color: "#7c4dff", glyph: "image", exts: ["png", "jpg", "jpeg", "gif", "webp", "bmp", "svg", "heic", "tif", "tiff", "ico", "avif"] },
  { type: "video", color: "#e0306a", glyph: "play", exts: ["mp4", "mov", "avi", "mkv", "webm", "wmv", "m4v"] },
  { type: "audio", color: "#c2410c", glyph: "note", exts: ["mp3", "wav", "m4a", "flac", "ogg", "aac", "wma"] },
  { type: "archive", color: "#8b5e3c", glyph: "zip", exts: ["zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz"] },
  {
    type: "code",
    color: "#0f8b8d",
    glyph: "code",
    exts: ["js", "jsx", "ts", "tsx", "json", "html", "htm", "css", "scss", "py", "java", "c", "h", "cpp", "cs",
      "php", "rb", "go", "rs", "sql", "xml", "yml", "yaml", "sh", "ps1", "bat", "md"],
  },
  { type: "app", color: "#495057", glyph: "window", exts: ["exe", "msi", "dmg", "app", "apk", "deb", "rpm"] },
  { type: "text", color: "#6c757d", glyph: "lines", exts: ["txt", "log", "ini", "cfg", "conf"] },
];

const BY_EXTENSION = Object.fromEntries(TYPES.flatMap((t) => t.exts.map((ext) => [ext, t])));
const GENERIC = { type: "generic", color: "#6c757d", glyph: "lines" };

const getExtension = (name = "") => {
  const dot = name.lastIndexOf(".");
  return dot > 0 ? name.slice(dot + 1).toLowerCase() : "";
};

// Type symbols, drawn in a 14x14 box
function Glyph({ kind, color }) {
  const stroke = { fill: "none", stroke: color, strokeWidth: 1.6, strokeLinecap: "round", strokeLinejoin: "round" };
  switch (kind) {
    case "image":
      return (
        <g>
          <rect x="1" y="2" width="12" height="10" rx="1.6" {...stroke} />
          <circle cx="9.3" cy="5.2" r="1.3" fill={color} />
          <path d="M1.8 11.2 5.2 7.4l2.4 2.6 1.6-1.6 3 2.8" {...stroke} />
        </g>
      );
    case "play":
      return (
        <g>
          <rect x="1" y="2" width="12" height="10" rx="2" {...stroke} />
          <path d="M5.6 4.8v4.4L9.4 7z" fill={color} stroke={color} strokeWidth="1" strokeLinejoin="round" />
        </g>
      );
    case "note":
      return (
        <g>
          <path d="M5.5 10.5V2.6l6.5-1.4v7.8" {...stroke} />
          <ellipse cx="3.9" cy="10.6" rx="2" ry="1.6" fill={color} />
          <ellipse cx="10.4" cy="9.2" rx="2" ry="1.6" fill={color} />
        </g>
      );
    case "code":
      return (
        <g {...stroke}>
          <path d="M4.6 3.6 1.4 7l3.2 3.4" />
          <path d="M9.4 3.6 12.6 7l-3.2 3.4" />
          <path d="M8.1 2.4 5.9 11.6" />
        </g>
      );
    case "grid":
      return (
        <g {...stroke}>
          <rect x="1" y="1.5" width="12" height="11" rx="1.4" />
          <path d="M1 5.2h12M1 8.8h12M5.3 1.5v11" />
        </g>
      );
    case "slides":
      return (
        <g>
          <rect x="1" y="1.5" width="12" height="9" rx="1.4" {...stroke} />
          <path d="M4 8V6.2M7 8V4.2M10 8V5.4" {...stroke} />
          <path d="M7 10.5V13" {...stroke} />
        </g>
      );
    case "zip":
      return (
        <g fill={color}>
          <rect x="5.4" y="0.6" width="3.2" height="1.8" rx="0.5" />
          <rect x="5.4" y="3.6" width="3.2" height="1.8" rx="0.5" />
          <rect x="5.4" y="6.6" width="3.2" height="1.8" rx="0.5" />
          <rect x="4.4" y="9.4" width="5.2" height="4" rx="1.2" />
        </g>
      );
    case "window":
      return (
        <g>
          <rect x="1" y="1.8" width="12" height="10.4" rx="1.6" {...stroke} />
          <path d="M1 4.8h12" {...stroke} />
          <circle cx="3.1" cy="3.3" r="0.6" fill={color} />
          <circle cx="5" cy="3.3" r="0.6" fill={color} />
        </g>
      );
    default: // text-like documents
      return (
        <g {...stroke}>
          <path d="M2 3h10M2 6h10M2 9h10M2 12h6" />
        </g>
      );
  }
}

function FolderIcon({ size, open }) {
  const id = useId();
  return (
    <svg className="file-type-icon folder" width={size} height={size} viewBox="0 0 48 48" aria-hidden="true" focusable="false">
      <defs>
        <linearGradient id={`${id}-front`} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="var(--fv-folder-front-top)" />
          <stop offset="1" stopColor="var(--fv-folder-front-bottom)" />
        </linearGradient>
      </defs>
      {/* Back panel with tab */}
      <path
        d="M4 11.5A3 3 0 0 1 7 8.5h11.3a3 3 0 0 1 2.1.9l2.6 2.6a3 3 0 0 0 2.1.9H41a3 3 0 0 1 3 3V38a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3Z"
        fill="var(--fv-folder-back)"
      />
      {/* Paper inside */}
      <rect x="8" y={open ? 12 : 15} width="32" height="20" rx="1.5" fill="var(--fv-folder-paper)" />
      {/* Front flap (tilted forward when open) */}
      {open ? (
        <path d="M7.6 19.5h36.8a2.4 2.4 0 0 1 2.3 3.1l-4.5 16.2A3 3 0 0 1 39.3 41H7a3 3 0 0 1-3-3V22a2.5 2.5 0 0 1 2.5-2.5Z" fill={`url(#${id}-front)`} />
      ) : (
        <path d="M4 20.5a3 3 0 0 1 3-3h34a3 3 0 0 1 3 3V38a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3Z" fill={`url(#${id}-front)`} />
      )}
      {/* Light edge along the top of the flap */}
      <path d={open ? "M7.6 20.4h36.6" : "M7 18.4h34"} stroke="var(--fv-folder-highlight)" strokeWidth="0.9" strokeLinecap="round" />
    </svg>
  );
}

function DocumentIcon({ name, size }) {
  const extension = getExtension(name);
  const kind = BY_EXTENSION[extension] ?? GENERIC;
  // The text label is only readable at larger sizes
  const label = size >= 36 && extension ? extension.toUpperCase().slice(0, 4) : null;
  const labelWidth = label ? 6 + label.length * 4.9 : 0;

  return (
    <svg className="file-type-icon document" width={size} height={size} viewBox="0 0 48 48" aria-hidden="true" focusable="false">
      {/* Page with a folded top-right corner */}
      <path
        d="M11.5 3.5H29L39.5 14v27.5a3 3 0 0 1-3 3h-25a3 3 0 0 1-3-3v-35a3 3 0 0 1 3-3Z"
        fill="var(--fv-file-page)"
        stroke="var(--fv-file-page-edge)"
        strokeWidth="1"
      />
      <path d="M29 3.5V11a3 3 0 0 0 3 3h7.5Z" fill="var(--fv-file-fold)" stroke="var(--fv-file-page-edge)" strokeWidth="1" strokeLinejoin="round" />

      {/* Type symbol: above the label at large sizes, centred on the page at small sizes */}
      <g transform={label ? "translate(17 13.5)" : "translate(13.2 17) scale(1.55)"}>
        <Glyph kind={kind.glyph} color={kind.color} />
      </g>

      {/* Extension label */}
      {label && (
        <g>
          <rect x="5.5" y="31" width={labelWidth} height="9.5" rx="2.2" fill={kind.color} />
          <text
            x={5.5 + labelWidth / 2}
            y="38.1"
            textAnchor="middle"
            fontSize="7"
            fontWeight="700"
            fontFamily="Segoe UI, -apple-system, Helvetica, Arial, sans-serif"
            fill="#fff"
            letterSpacing="0.25"
          >
            {label}
          </text>
        </g>
      )}
    </svg>
  );
}

export default function FileTypeIcon({ name, isDirectory = false, size = 48, open = false }) {
  return isDirectory ? <FolderIcon size={size} open={open} /> : <DocumentIcon name={name} size={size} />;
}
