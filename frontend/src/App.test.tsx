import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from './App';

function stubApi(statusByPath: Record<string, { status: number; body?: unknown }>) {
  return vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
    const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
    const match = Object.entries(statusByPath).find(([key]) => path.startsWith(key));
    if (match === undefined) {
      throw new Error(`Unexpected fetch: ${path}`);
    }
    return new Response(JSON.stringify(match[1].body ?? {}), { status: match[1].status });
  });
}

describe('App auth flow', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    document.cookie = 'ha_csrf=; Max-Age=0; Path=/';
  });

  it('shows the login form when the session probe is rejected', async () => {
    vi.stubGlobal('fetch', stubApi({ '/api/metrics': { status: 401 } }));

    render(<App />);

    await waitFor(() => {
      expect(screen.getByLabelText('Password')).toBeInTheDocument();
    });
  });

  it('shows the authenticated view when a valid session exists', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
        if (path === '/api/metrics' || path === '/api/system/info' || path === '/api/disks' || path === '/api/services') {
          return new Response('{}', { status: 200 });
        }
        throw new Error(`Unexpected fetch: ${path}`);
      }),
    );

    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('navigation', { name: 'Main navigation' })).toBeInTheDocument();
    });
  });

  it('transitions to the authenticated view after a successful login', async () => {
    const user = userEvent.setup();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
        const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
        if (path === '/api/metrics') {
          return new Response('{}', { status: 401 });
        }
        if (path === '/api/auth/login' && init?.method === 'POST') {
          return new Response(JSON.stringify({ status: 'authenticated' }), { status: 200 });
        }
        throw new Error(`Unexpected fetch: ${path}`);
      }),
    );

    render(<App />);

    await user.type(await screen.findByLabelText('Password'), 'password-123');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => {
      expect(screen.getByRole('navigation', { name: 'Main navigation' })).toBeInTheDocument();
    });
  });

  it('returns to the login form after logout', async () => {
    const user = userEvent.setup();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
        if (path === '/api/metrics' || path === '/api/system/info' || path === '/api/disks' || path === '/api/services') {
          return new Response('{}', { status: 200 });
        }
        if (path === '/api/auth/logout') {
          return new Response(null, { status: 204 });
        }
        throw new Error(`Unexpected fetch: ${path}`);
      }),
    );

    render(<App />);

    await user.click(await screen.findByRole('button', { name: 'Sign out' }));

    await waitFor(() => {
      expect(screen.getByLabelText('Password')).toBeInTheDocument();
    });
  });

  it('sends the CSRF header on the login request', async () => {
    const user = userEvent.setup();
    document.cookie = 'ha_csrf=prelogin-token; Path=/';
    const fetchMock = vi.fn().mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
      if (path === '/api/metrics') {
        return new Response('{}', { status: 401 });
      }
      if (path === '/api/auth/login') {
        const headers = new Headers(init?.headers);
        expect(headers.get('X-CSRF-Token')).toBe('prelogin-token');
        return new Response(JSON.stringify({ status: 'authenticated' }), { status: 200 });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    await user.type(await screen.findByLabelText('Password'), 'password-123');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => {
      expect(fetchMock.mock.calls.some(([path]) => String(path).includes('/api/auth/login'))).toBe(true);
    });
  });
});
