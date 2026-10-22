import { act, renderHook } from '@testing-library/react';
import { ApiError } from '../api/client';
import { usePolling } from './usePolling';

describe('usePolling', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('fetches immediately on mount', async () => {
    const fetcher = vi.fn().mockResolvedValue({ value: 1 });

    const { result } = renderHook(() => usePolling(fetcher, 10_000));

    await act(async () => {});
    expect(fetcher).toHaveBeenCalledTimes(1);
    expect(result.current.data).toEqual({ value: 1 });
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('polls again on the interval', async () => {
    const fetcher = vi.fn().mockResolvedValue('data');

    renderHook(() => usePolling(fetcher, 10_000));
    await act(async () => {});
    expect(fetcher).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });
    expect(fetcher).toHaveBeenCalledTimes(2);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });
    expect(fetcher).toHaveBeenCalledTimes(3);
  });

  it('stops polling after unmount', async () => {
    const fetcher = vi.fn().mockResolvedValue('data');

    const { unmount } = renderHook(() => usePolling(fetcher, 10_000));
    await act(async () => {});
    unmount();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });
    expect(fetcher).toHaveBeenCalledTimes(1);
  });

  it('surfaces a session-expired message on 401', async () => {
    const fetcher = vi.fn().mockRejectedValue(new ApiError(401, 'unauthorized', 'Sign in required.'));

    const { result } = renderHook(() => usePolling(fetcher, 10_000));
    await act(async () => {});

    expect(result.current.error).toBe('Session expired. Sign in again.');
    expect(result.current.loading).toBe(false);
  });

  it('surfaces a generic message on other errors and keeps previous data', async () => {
    const fetcher = vi
      .fn()
      .mockResolvedValueOnce({ version: 1 })
      .mockRejectedValueOnce(new Error('network down'));

    const { result } = renderHook(() => usePolling(fetcher, 10_000));
    await act(async () => {});
    expect(result.current.data).toEqual({ version: 1 });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });
    expect(result.current.error).toBe('Data unavailable. Retrying…');
    expect(result.current.data).toEqual({ version: 1 });
  });
});
