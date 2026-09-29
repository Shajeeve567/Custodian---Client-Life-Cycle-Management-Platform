import { useEffect, useRef } from 'react';

/**
 * Keeps a view current without a manual reload: calls `refresh` when the tab becomes visible again and
 * every `intervalMs` while it is visible (paused in a hidden tab). A call is skipped while the previous
 * one is still running, so slow responses never pile up.
 */
export function useAutoRefresh(refresh: () => Promise<unknown>, intervalMs: number, enabled = true): void {
    const refreshRef = useRef(refresh);
    const inFlight = useRef(false);

    useEffect(() => {
        refreshRef.current = refresh;
    }, [refresh]);

    useEffect(() => {
        if (!enabled) return;

        const run = async () => {
            if (inFlight.current || document.visibilityState !== 'visible') return;
            inFlight.current = true;
            try {
                await refreshRef.current();
            } catch {
                // Background refresh: the view keeps its last data; the next tick tries again.
            } finally {
                inFlight.current = false;
            }
        };

        const onVisibility = () => {
            if (document.visibilityState === 'visible') void run();
        };

        const timer = window.setInterval(() => void run(), intervalMs);
        document.addEventListener('visibilitychange', onVisibility);
        return () => {
            window.clearInterval(timer);
            document.removeEventListener('visibilitychange', onVisibility);
        };
    }, [intervalMs, enabled]);
}
