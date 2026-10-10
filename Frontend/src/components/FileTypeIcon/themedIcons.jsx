import { useId } from "react";

// Our own folder and page icons in the spirit of Windows, macOS and Ubuntu desktops. They're original
// drawings (shapes and colour schemes only), coloured through the --ic-* variables set per theme in
// FileTypeIcon.css. The type glyph and label colours come from FileTypeIcon.

// Folder outlines in a 48x48 box: back panel, front panel (closed / open)
const FOLDERS = {
  windows: {
    back: "M4 9.5A2.5 2.5 0 0 1 6.5 7h11a2.5 2.5 0 0 1 1.8.8L22 11h19.5a2.5 2.5 0 0 1 2.5 2.5v24a2.5 2.5 0 0 1-2.5 2.5h-35A2.5 2.5 0 0 1 4 37.5Z",
    front: "M4 17.5A2.5 2.5 0 0 1 6.5 15h35a2.5 2.5 0 0 1 2.5 2.5v20a2.5 2.5 0 0 1-2.5 2.5h-35A2.5 2.5 0 0 1 4 37.5Z",
    frontOpen: "M7 18.5h37.4a1.8 1.8 0 0 1 1.7 2.3l-4.3 16.9a2.5 2.5 0 0 1-2.4 1.9H6.5A2.5 2.5 0 0 1 4 37.1V21.5A3 3 0 0 1 7 18.5Z",
    paper: false,
  },
  macos: {
    back: "M4 12a4 4 0 0 1 4-4h8.5a4 4 0 0 1 2.9 1.2l1.5 1.6a4 4 0 0 0 2.9 1.2H40a4 4 0 0 1 4 4v20a4 4 0 0 1-4 4H8a4 4 0 0 1-4-4Z",
    front: "M4 17.5a4 4 0 0 1 4-4h32a4 4 0 0 1 4 4V37a4 4 0 0 1-4 4H8a4 4 0 0 1-4-4Z",
    frontOpen: "M8 17.5h35.6a2.4 2.4 0 0 1 2.3 3.1l-4.2 15.8a4 4 0 0 1-3.8 3H8a4 4 0 0 1-4-4V21.5a4 4 0 0 1 4-4Z",
    paper: true,
  },
  ubuntu: {
    back: "M4 8h14l4 4h22v26a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2Z",
    front: "M4 16h40v22a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2Z",
    frontOpen: "M7 19h38.5l-4.7 19.5a2 2 0 0 1-1.9 1.5H6a2 2 0 0 1-2-2V21a2 2 0 0 1 2-2Z",
    paper: true,
  },
};

export function ThemedFolder({ theme, size, open, glyph }) {
  const id = useId();
  const shape = FOLDERS[theme];
  return (
    <svg
      className={`file-type-icon folder icon-theme-${theme}`}
      width={size}
      height={size}
      viewBox="0 0 48 48"
      aria-hidden="true"
      focusable="false"
      data-icon-theme={theme}
    >
      <defs>
        <linearGradient id={`${id}-front`} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="var(--ic-front-top)" />
          <stop offset="1" stopColor="var(--ic-front-bottom)" />
        </linearGradient>
      </defs>
      <path d={shape.back} fill="var(--ic-back)" />
      {shape.paper && <rect x="9" y={open ? 13 : 16} width="30" height="18" rx="1.5" fill="var(--ic-paper)" />}
      <path d={open ? shape.frontOpen : shape.front} fill={`url(#${id}-front)`} />
      {theme === "ubuntu" && <rect x="4" y="37" width="40" height="3" fill="var(--ic-base)" opacity="0.55" />}
      {theme === "macos" && <path d="M8 17.7h32" stroke="var(--ic-highlight)" strokeWidth="1" strokeLinecap="round" />}
      {glyph && (
        <g transform={open ? "translate(17.5 24)" : "translate(17 23)"} opacity="0.9">
          {glyph}
        </g>
      )}
    </svg>
  );
}

// Page outlines: Windows has a small fold and sharp-ish corners, macOS is rounder, Ubuntu is squarer with a type bar
const PAGES = {
  windows: { page: "M10 4h19l10 10v28.5a1.5 1.5 0 0 1-1.5 1.5h-27.5a1.5 1.5 0 0 1-1.5-1.5v-37a1.5 1.5 0 0 1 1.5-1.5Z", fold: "M29 4v8.5a1.5 1.5 0 0 0 1.5 1.5H39Z" },
  macos: { page: "M12 3.5h17l10.5 10.5v28a3.5 3.5 0 0 1-3.5 3.5H12a3.5 3.5 0 0 1-3.5-3.5V7a3.5 3.5 0 0 1 3.5-3.5Z", fold: "M29 3.5V11a3 3 0 0 0 3 3h7.5Z" },
  ubuntu: { page: "M9 3.5h20.5L39 13v30.5H9Z", fold: "M29.5 3.5V13H39Z" },
};

export function ThemedDocument({ theme, size, label, kind, glyph }) {
  const shape = PAGES[theme];
  const labelled = !!label;
  return (
    <svg
      className={`file-type-icon document icon-theme-${theme}`}
      width={size}
      height={size}
      viewBox="0 0 48 48"
      aria-hidden="true"
      focusable="false"
      data-icon-theme={theme}
    >
      <path d={shape.page} fill="var(--ic-page)" stroke="var(--ic-page-edge)" strokeWidth="1" strokeLinejoin="round" />
      <path d={shape.fold} fill="var(--ic-fold)" stroke="var(--ic-page-edge)" strokeWidth="1" strokeLinejoin="round" />
      <g transform={labelled ? "translate(17 14)" : "translate(13.2 17) scale(1.55)"}>
        {glyph}
      </g>
      {labelled && theme === "ubuntu" && (
        <g>
          <rect x="9" y="34" width="30" height="9.5" fill={kind.color} />
          <text x="24" y="41.2" textAnchor="middle" fontSize="7" fontWeight="700" fontFamily="Ubuntu, Cantarell, Helvetica, Arial, sans-serif" fill="#fff" letterSpacing="0.3">
            {label}
          </text>
        </g>
      )}
      {labelled && theme === "macos" && (
        <text x="24" y="40.5" textAnchor="middle" fontSize="7.5" fontWeight="700" fontFamily="-apple-system, Helvetica, Arial, sans-serif" fill={kind.color} letterSpacing="0.3">
          {label}
        </text>
      )}
      {labelled && theme === "windows" && (
        <g>
          <rect x="7" y="33" width={5 + label.length * 4.6} height="9" rx="1.2" fill={kind.color} />
          <text x={7 + (5 + label.length * 4.6) / 2} y="39.8" textAnchor="middle" fontSize="6.5" fontWeight="600" fontFamily="Segoe UI, Helvetica, Arial, sans-serif" fill="#fff">
            {label}
          </text>
        </g>
      )}
    </svg>
  );
}
