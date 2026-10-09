import { MdClose, MdMenu } from "react-icons/md";

// Top-bar button that opens and closes the folder drawer on phones and tablets
export default function NavDrawerToggle({ open, onToggle }) {
  const label = open ? "Close folders" : "Show folders";
  return (
    <button
      type="button"
      className="nav-drawer-toggle"
      title={label}
      aria-label={label}
      aria-expanded={open}
      onClick={onToggle}
    >
      {open ? <MdClose size={22} /> : <MdMenu size={22} />}
    </button>
  );
}
