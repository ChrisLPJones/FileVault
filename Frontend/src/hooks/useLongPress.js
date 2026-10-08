import { useEffect, useRef } from "react";

const DELAY_MS = 500;
const MOVE_TOLERANCE_PX = 10;

// Touch and hold: calls onLongPress({ clientX, clientY }) after half a second without moving
// (so a scroll doesn't count). Spread `handlers` on the element. `wasLongPress()` is true once
// for the click that follows a long press, so that click can be ignored.
export const useLongPress = (onLongPress) => {
    const timer = useRef(null);
    const start = useRef(null);
    const fired = useRef(false);
    const firedAt = useRef(0);

    const cancel = () => {
        clearTimeout(timer.current);
        timer.current = null;
    };

    useEffect(() => cancel, []);

    const handlers = {
        onTouchStart: (event) => {
            if (event.touches.length !== 1) return cancel();
            const { clientX, clientY } = event.touches[0];
            start.current = { clientX, clientY };
            fired.current = false;
            cancel();
            timer.current = setTimeout(() => {
                timer.current = null;
                fired.current = true;
                firedAt.current = Date.now();
                onLongPress(start.current);
            }, DELAY_MS);
        },
        onTouchMove: (event) => {
            const touch = event.touches[0];
            if (!start.current || !touch) return;
            if (Math.hypot(touch.clientX - start.current.clientX, touch.clientY - start.current.clientY) > MOVE_TOLERANCE_PX)
                cancel();
        },
        onTouchEnd: (event) => {
            cancel();
            // No mouse events or click after a long press (they would close the menu again)
            if (fired.current && event.cancelable) event.preventDefault();
        },
        onTouchCancel: cancel,
    };

    // Some browsers still send a click; ignore one that comes straight after a long press
    const wasLongPress = () => {
        const result = fired.current && Date.now() - firedAt.current < 1000;
        fired.current = false;
        return result;
    };

    return { handlers, wasLongPress };
};
