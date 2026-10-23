import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '../api/client';
import { ServiceActions } from './ServiceActions';

const service = {
  name: 'nginx.service',
  loadState: 'loaded',
  activeState: 'active',
  subState: 'running',
  enabledState: 'enabled',
  since: '2026-10-20T08:14:03Z',
};

function mockFetch(handlers: Record<string, unknown>) {
  return vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
    const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
    const handler = handlers[path];
    if (handler === undefined) {
      throw new Error(`Unexpected fetch: ${path}`);
    }
    if (handler instanceof ApiError) {
      return new Response(JSON.stringify({ error: { code: handler.code, message: handler.message } }), {
        status: handler.status,
      });
    }
    return new Response(JSON.stringify(handler), { status: 200 });
  });
}

describe('ServiceActions', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders no controls when mutation capability is unavailable (D24)', () => {
    vi.stubGlobal('fetch', vi.fn());

    render(<ServiceActions service={service} mutationCapabilityAvailable={false} onActionCompleted={() => {}} />);

    expect(screen.queryByTestId('service-actions-nginx.service')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Start nginx.service' })).not.toBeInTheDocument();
  });

  it('start executes directly and reports success', async () => {
    const user = userEvent.setup();
    const onActionCompleted = vi.fn();
    vi.stubGlobal(
      'fetch',
      mockFetch({
        '/api/services/nginx.service/start': { unit: 'nginx.service', action: 'start', outcome: 'Success', completedAt: '2026-10-23T00:00:00Z' },
      }),
    );

    render(<ServiceActions service={service} mutationCapabilityAvailable onActionCompleted={onActionCompleted} />);

    await user.click(screen.getByRole('button', { name: 'Start nginx.service' }));

    await waitFor(() => {
      expect(screen.getByTestId('action-message-nginx.service')).toHaveTextContent('start completed');
    });
    expect(onActionCompleted).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('confirmation-dialog')).not.toBeInTheDocument();
  });

  it('stop opens the confirmation dialog and does not execute until confirmed', async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
      const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
      if (path === '/api/services/nginx.service/stop') {
        return new Response(
          JSON.stringify({
            confirmationId: 'tok-1',
            unit: 'nginx.service',
            action: 'stop',
            expiresAt: new Date(Date.now() + 60_000).toISOString(),
          }),
          { status: 200 },
        );
      }
      if (path === '/api/service-actions/tok-1/confirm') {
        return new Response(
          JSON.stringify({ unit: 'nginx.service', action: 'stop', outcome: 'Success', completedAt: '2026-10-23T00:00:00Z' }),
          { status: 200 },
        );
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<ServiceActions service={service} mutationCapabilityAvailable onActionCompleted={() => {}} />);

    await user.click(screen.getByRole('button', { name: 'Stop nginx.service' }));

    const dialog = await screen.findByTestId('confirmation-dialog');
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(dialog).toHaveTextContent('stop');
    expect(dialog).toHaveTextContent('nginx.service');
    // Only the request call happened; confirm was not called yet.
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await user.click(screen.getByRole('button', { name: 'Confirm stop' }));

    await waitFor(() => {
      expect(screen.getByTestId('action-message-nginx.service')).toHaveTextContent('stop completed');
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(screen.queryByTestId('confirmation-dialog')).not.toBeInTheDocument();
  });

  it('cancel path discards the confirmation without executing', async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
      const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
      if (path === '/api/services/nginx.service/restart') {
        return new Response(
          JSON.stringify({
            confirmationId: 'tok-2',
            unit: 'nginx.service',
            action: 'restart',
            expiresAt: new Date(Date.now() + 60_000).toISOString(),
          }),
          { status: 200 },
        );
      }
      if (path === '/api/service-actions/tok-2/cancel') {
        return new Response(JSON.stringify({ status: 'cancelled' }), { status: 200 });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<ServiceActions service={service} mutationCapabilityAvailable onActionCompleted={() => {}} />);

    await user.click(screen.getByRole('button', { name: 'Restart nginx.service' }));
    await waitFor(() => {
      expect(screen.getByTestId('confirmation-dialog')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() => {
      expect(screen.queryByTestId('confirmation-dialog')).not.toBeInTheDocument();
    });
    // Only request + cancel; confirm was never called.
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls.every(([input]) => !String(input).includes('/confirm'))).toBe(true);
  });

  it('shows a generic error when the action request fails', async () => {
    const user = userEvent.setup();
    vi.stubGlobal(
      'fetch',
      mockFetch({
        '/api/services/nginx.service/start': new ApiError(503, 'capability_unavailable', 'Service control is unavailable.'),
      }),
    );

    render(<ServiceActions service={service} mutationCapabilityAvailable onActionCompleted={() => {}} />);

    await user.click(screen.getByRole('button', { name: 'Start nginx.service' }));

    await waitFor(() => {
      expect(screen.getByTestId('action-message-nginx.service')).toHaveTextContent('start failed');
    });
    expect(screen.getByRole('alert')).toBeInTheDocument();
  });

  it('shows a generic error and closes the dialog when confirm fails (e.g. expired/consumed)', async () => {
    const user = userEvent.setup();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
        if (path === '/api/services/nginx.service/stop') {
          return new Response(
            JSON.stringify({
              confirmationId: 'tok-3',
              unit: 'nginx.service',
              action: 'stop',
              expiresAt: new Date(Date.now() + 60_000).toISOString(),
            }),
            { status: 200 },
          );
        }
        if (path === '/api/service-actions/tok-3/confirm') {
          return new Response(
            JSON.stringify({ error: { code: 'invalid_confirmation', message: 'The confirmation is no longer valid.' } }),
            { status: 409 },
          );
        }
        throw new Error(`Unexpected fetch: ${path}`);
      }),
    );

    render(<ServiceActions service={service} mutationCapabilityAvailable onActionCompleted={() => {}} />);

    await user.click(screen.getByRole('button', { name: 'Stop nginx.service' }));
    await waitFor(() => {
      expect(screen.getByTestId('confirmation-dialog')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: 'Confirm stop' }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('no longer valid');
    });
    expect(screen.queryByTestId('confirmation-dialog')).not.toBeInTheDocument();
  });

  it('disables all buttons while a request is in flight', async () => {
    let resolveStart: ((response: Response) => void) | undefined;
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(
        () =>
          new Promise<Response>((resolve) => {
            resolveStart = resolve;
          }),
      ),
    );

    render(<ServiceActions service={service} mutationCapabilityAvailable onActionCompleted={() => {}} />);

    await userEvent.click(screen.getByRole('button', { name: 'Start nginx.service' }));

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Start nginx.service' })).toBeDisabled();
      expect(screen.getByRole('button', { name: 'Stop nginx.service' })).toBeDisabled();
      expect(screen.getByRole('button', { name: 'Restart nginx.service' })).toBeDisabled();
    });

    resolveStart!(new Response(JSON.stringify({ unit: 'nginx.service', action: 'start', outcome: 'Success', completedAt: '2026-10-23T00:00:00Z' }), { status: 200 }));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Start nginx.service' })).toBeEnabled();
    });
  });
});
