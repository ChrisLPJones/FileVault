import { MdKeyboardArrowRight } from "react-icons/md";
import "./NavSection.scss";

// A titled section of the folder pane that collapses when its heading is clicked. The caller
// decides how to hide the body (render it only while expanded, or wrap it in <Collapse>).
export default function NavSection({ title, expanded, onToggle, className = "", children }) {
  return (
    <section className={`nav-section ${className}`} aria-label={title}>
      <button type="button" className="nav-section-heading" aria-expanded={expanded} onClick={onToggle}>
        <MdKeyboardArrowRight size={18} className={`nav-section-arrow ${expanded ? "open" : ""}`} />
        {title}
      </button>
      {children}
    </section>
  );
}
