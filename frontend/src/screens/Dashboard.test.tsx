import { render, screen, waitFor } from '@testing-library/react';
import { ApiError } from '../api/client';
import { Dashboard } from './Dashboard';

function mockApi(handlers: Record<string, unknown>) {
  return vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
    const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
    const handler = (handlers as Record<string, unknown>)[path];
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

const metricsPayload = {
  isAvailable: true,
  unavailableReason: null,
  cpu: {
    usagePercent: 12.4,
    cores: 2,
    loadAverage1m: 0.31,
    loadAverage5m: 0.22,
    loadAverage15m: 0.18,
    sampledAt: '2026-10-21T12:00:00Z',
  },
  memory: { totalBytes: 2147483648, usedBytes: 1073741824, availableBytes: 1073741824, usagePercent: 50.0 },
  swap: { totalBytes: 536870912, usedBytes: 0, usagePercent: 0.0 },
  uptimeSeconds: 864000,
};

const systemInfoPayload = {
  isAvailable: true,
  unavailableReason: null,
  hostname: 'vps01',
  operatingSystem: 'Debian GNU/Linux 12',
  kernelVersion: '6.1.0-18-amd64',
  architecture: 'x86-64',
};

const disksPayload = {
  isAvailable: true,
  unavailableReason: null,
  disks: [
    {
      device: '/dev/sda1',
      mountPoint: '/',
      fileSystem: 'ext4',
      totalBytes: 40 * 1024 ** 3,
      usedBytes: 10 * 1024 ** 3,
      availableBytes: 30 * 1024 ** 3,
      usagePercent: 25.0,
    },
  ],
};

const servicesPayload = {
  services: [
    {
      name: 'nginx',
      loadState: 'loaded',
      activeState: 'active',
      subState: 'running',
      enabledState: 'enabled',
      since: '2026-10-20T08:14:03Z',
    },
  ],
  capabilityAvailable: true,
};

describe('Dashboard', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders metric cards, disks and services from the API', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(
        async (input: RequestInfo | URL): Promise<Response> => {
          const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
          const bodies: Record<string, unknown> = {
            '/api/metrics': metricsPayload,
            '/api/system/info': systemInfoPayload,
            '/api/disks': disksPayload,
            '/api/services': servicesPayload,
          };
          if (path in bodies) {
            return new Response(JSON.stringify(bodies[path]), { status: 200 });
          }
          throw new Error(`Unexpected fetch: ${path}`);
        },
      ),
    );

    render(<Dashboard />);

    await waitFor(() => {
      expect(screen.getByTestId('cpu-usage')).toHaveTextContent('12.4%');
    });
    expect(screen.getByTestId('memory-usage')).toHaveTextContent('1.0 GiB / 2.0 GiB');
    expect(screen.getByTestId('uptime')).toHaveTextContent('10 d 0 h');
    expect(screen.getByText('vps01')).toBeInTheDocument();
    expect(screen.getByText('nginx')).toBeInTheDocument();
    expect(screen.getByText('/dev/sda1')).toBeInTheDocument();
  });

  it('shows the structured unavailable banner when metrics report unavailability', async () => {
    vi.stubGlobal(
      'fetch',
      mockApi({
        '/api/metrics': {
          isAvailable: false,
          unavailableReason: 'procfs not mounted',
          cpu: null,
          memory: null,
          swap: null,
          uptimeSeconds: null,
        },
        '/api/system/info': systemInfoPayload,
        '/api/disks': disksPayload,
        '/api/services': servicesPayload,
      }),
    );

    render(<Dashboard />);

    await waitFor(() => {
      expect(screen.getAllByTestId('unavailable-banner').length).toBeGreaterThan(0);
    });
  });

  it('shows the capability banner when systemd status is unavailable', async () => {
    vi.stubGlobal(
      'fetch',
      mockApi({
        '/api/metrics': metricsPayload,
        '/api/system/info': systemInfoPayload,
        '/api/disks': disksPayload,
        '/api/services': { services: [], capabilityAvailable: false },
      }),
    );

    render(<Dashboard />);

    await waitFor(() => {
      expect(screen.getByTestId('capability-banner')).toHaveTextContent(/systemd status is not available/i);
    });
  });

  it('shows a session-expired alert when a poll returns 401', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
        if (path === '/api/metrics') {
          return new Response(JSON.stringify({ error: { code: 'unauthorized', message: 'x' } }), { status: 401 });
        }
        if (path === '/api/system/info') {
          return new Response(JSON.stringify(systemInfoPayload), { status: 200 });
        }
        if (path === '/api/disks') {
          return new Response(JSON.stringify(disksPayload), { status: 200 });
        }
        if (path === '/api/services') {
          return new Response(JSON.stringify(servicesPayload), { status: 200 });
        }
        throw new Error(`Unexpected fetch: ${path}`);
      }),
    );

    render(<Dashboard />);

    await waitFor(() => {
      expect(screen.getByTestId('metrics-error')).toHaveTextContent(/session expired/i);
    });
  });
});

