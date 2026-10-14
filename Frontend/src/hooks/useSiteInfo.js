import { useEffect, useState } from "react";
import { getSiteInfoAPI } from "../api/siteAPI";

// The public site info (see siteAPI), fetched once and shared by everything that shows it. A failed
// fetch isn't remembered, so the next page asks again. privacy is null until loaded and on a
// self-hosted install, so "privacy is set" means "this is a hosted site".
let request = null;

const fetchOnce = () => {
    if (!request) {
        request = getSiteInfoAPI().catch((err) => {
            request = null;
            throw err;
        });
    }
    return request;
};

// For tests
export const resetSiteInfo = () => {
    request = null;
};

export function useSiteInfo() {
    const [state, setState] = useState({ loading: true, error: false, privacy: null });

    useEffect(() => {
        let cancelled = false;
        fetchOnce()
            .then((info) => !cancelled && setState({ loading: false, error: false, privacy: info?.privacy ?? null }))
            .catch(() => !cancelled && setState({ loading: false, error: true, privacy: null }));
        return () => {
            cancelled = true;
        };
    }, []);

    return state;
}
