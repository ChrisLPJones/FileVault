import { useEffect, useRef, useState } from "react";
import { MdMoreHoriz } from "react-icons/md";
import { FaCheck } from "react-icons/fa6";

// "⋯" menu holding the less-used toolbar actions on narrow screens (where those buttons are
// hidden; see Mobile.scss). items: [{ text, icon, onClick, checked?, divider? }]
export default function ToolbarOverflow({ items }) {
  const [open, setOpen] = useState(false);
  const ref = useRef(null);

  useEffect(() => {
    if (!open) return undefined;
    const onPointerDown = (event) => !ref.current?.contains(event.target) && setOpen(false);
    const onKeyDown = (event) => event.key === "Escape" && setOpen(false);
    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  if (items.length === 0) return null;

  return (
    <div className="toolbar-overflow" ref={ref}>
      <button
        type="button"
        className="item-action icon-only"
        title="More actions"
        aria-label="More actions"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
      >
        <MdMoreHoriz size={22} />
      </button>
      {open && (
        <ul className="toolbar-overflow-menu" role="menu">
          {items.map((item) => (
            <li key={item.text} role="none" className={item.divider ? "divider-above" : ""}>
              <button
                type="button"
                role="menuitem"
                onClick={() => {
                  setOpen(false);
                  item.onClick();
                }}
              >
                <span className="toolbar-overflow-icon">{item.icon}</span>
                <span>{item.text}</span>
                {item.checked && <FaCheck size={12} className="toolbar-overflow-check" aria-label="(current)" />}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
