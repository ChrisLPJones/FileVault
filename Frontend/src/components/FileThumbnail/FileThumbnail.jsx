import { useEffect, useRef, useState } from "react";
import FileTypeIcon from "../FileTypeIcon/FileTypeIcon";
import { canHaveThumbnail, fetchThumbnailBlob } from "../../api/thumbnailAPI";
import "./FileThumbnail.css";

// Files the server has no thumbnail for, so scrolling back past them doesn't ask again
const noThumbnail = new Set();

const supportsObserver = () => typeof IntersectionObserver !== "undefined";

// An image file's thumbnail, or its file type icon until the thumbnail has loaded (and for
// everything else). Thumbnails are only fetched once the tile is on screen. Render with
// key={file._id} so a tile reused for another file starts again.
export default function FileThumbnail({ file, size }) {
    const ref = useRef(null);
    const wanted = canHaveThumbnail(file) && !noThumbnail.has(file._id);
    const [visible, setVisible] = useState(() => !supportsObserver());
    const [url, setUrl] = useState(null);

    // Wait until the tile is (nearly) scrolled into view
    useEffect(() => {
        if (!wanted || visible || !ref.current) return undefined;
        const observer = new IntersectionObserver(
            (entries) => {
                if (entries.some((entry) => entry.isIntersecting)) {
                    setVisible(true);
                    observer.disconnect();
                }
            },
            { rootMargin: "200px" }
        );
        observer.observe(ref.current);
        return () => observer.disconnect();
    }, [wanted, visible]);

    // Load it through the authenticated API client, and release the object URL afterwards
    useEffect(() => {
        if (!wanted || !visible) return undefined;
        let cancelled = false;
        let objectUrl = null;

        fetchThumbnailBlob(file._id)
            .then((blob) => {
                if (cancelled) return;
                objectUrl = URL.createObjectURL(blob);
                setUrl(objectUrl);
            })
            .catch((error) => {
                if (error?.response?.status === 404) noThumbnail.add(file._id);
            });

        return () => {
            cancelled = true;
            if (objectUrl) URL.revokeObjectURL(objectUrl);
        };
    }, [wanted, visible, file._id]);

    return (
        <span ref={ref} className="file-thumbnail" style={{ height: size }}>
            {url ? (
                <img src={url} alt="" draggable={false} style={{ maxHeight: size, maxWidth: Math.round(size * 1.9) }} />
            ) : (
                <FileTypeIcon name={file.name} path={file.path} isDirectory={file.isDirectory} size={size} />
            )}
        </span>
    );
}
