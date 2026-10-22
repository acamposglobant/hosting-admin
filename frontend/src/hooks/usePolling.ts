import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../api/client';

export interface PollingState<T> {
  data: T | null;
  error: string | null;
  /** True until the first successful (or failed) fetch completes. */
  loading: boolean;
  /** True while a background refresh is in flight after the first load. */
  refreshing: boolean;
}

/**
 * Polling hook (plan §5, D10): fetches immediately, then on a fixed interval.
 * Cleans up on unmount; never overlaps fetches. Errors are surfaced to the
 * caller — a 401/403 is reported as "session expired" so the screen can point
 * back to login.
 */
export function usePolling<T>(
  fetcher: () => Promise<T>,
  intervalMs: number,
  refreshKey?: unknown,
): PollingState<T> {
  const [state, setState] = useState<PollingState<T>>({
    data: null,
    error: null,
    loading: true,
    refreshing: false,
  });

  const fetcherRef = useRef(fetcher);
  fetcherRef.current = fetcher;
  const inFlight = useRef(false);

  useEffect(() => {
    let cancelled = false;

    async function poll() {
      if (inFlight.current) {
        return;
      }
      inFlight.current = true;
      setState((previous) => ({ ...previous, refreshing: previous.data !== null }));
      try {
        const data = await fetcherRef.current();
        if (!cancelled) {
          setState({ data, error: null, loading: false, refreshing: false });
        }
      } catch (error) {
        if (!cancelled) {
          const message =
            error instanceof ApiError && (error.status === 401 || error.status === 403)
              ? 'Session expired. Sign in again.'
              : 'Data unavailable. Retrying…';
          setState((previous) => ({
            data: previous.data,
            error: message,
            loading: false,
            refreshing: false,
          }));
        }
      } finally {
        inFlight.current = false;
      }
    }

    void poll();
    const handle = setInterval(() => void poll(), intervalMs);
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
    // The fetcher is read through fetcherRef, so callers may pass inline
    // closures (e.g. () => fetchAuditPage(skip, take)) without re-triggering
    // the effect on every render. Polling restarts on interval change or when
    // the refreshKey changes (e.g. a new audit page number).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [intervalMs, refreshKey]);

  return state;
}
