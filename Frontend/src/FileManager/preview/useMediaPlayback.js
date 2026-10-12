import { useState } from "react";

// Watches a <video> or <audio> element that plays a previewed file, since the browser only plays
// the formats it has decoders for (an iPhone's HEVC video, say) and says nothing else about it.
// Spread `props` onto the element. `status` is "ok", "error" (the file can't be played at all) or
// "no-picture" (a video that has no picture the browser can show, which plays as a black frame,
// maybe with sound). Create it where the element is, so each file starts with "ok".
export function useMediaPlayback(kind) {
  const [status, setStatus] = useState("ok");
  const props = {
    onError: () => setStatus("error"),
    onLoadedMetadata: (event) => {
      if (kind === "video" && event.currentTarget.videoWidth === 0) setStatus("no-picture");
    },
  };
  return { status, props };
}

// What to tell the user for a status other than "ok"
export const mediaProblemText = (kind, status) => {
  if (status === "error") return `This ${kind} format can't be played in your browser.`;
  if (status === "no-picture") return "No picture? This video's format may not be supported by your browser.";
  return "";
};
