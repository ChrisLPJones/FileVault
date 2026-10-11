import { useEffect, useState } from "react";
import { getSiteInfoAPI } from "../api/siteAPI";

// Public site information (the abuse contact), fetched once per page load and shared by every page
// that shows it. A failure is swallowed: the pages simply show nothing extra.
const NONE = { abuseContactEmail: null };

let pending = null;

const loadSiteInfo = () => {
    pending ??= getSiteInfoAPI()
        .then((info) => ({ abuseContactEmail: info?.abuseContactEmail || null }))
        .catch(() => NONE);
    return pending;
};

// For tests: forget the cached result
export const resetSiteInfo = () => {
    pending = null;
};

export const useSiteInfo = () => {
    const [info, setInfo] = useState(NONE);

    useEffect(() => {
        let active = true;
        loadSiteInfo().then((loaded) => active && setInfo(loaded));
        return () => {
            active = false;
        };
    }, []);

    return info;
};
