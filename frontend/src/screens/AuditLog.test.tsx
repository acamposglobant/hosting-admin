import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuditLog } from './AuditLog';

const auditPage = {
  items: [
    {
      timestampUtc: '2026-10-21T12:00:00Z',
      actor: 'admin',
      action: 'auth.login',
      outcome: 'Success',
      sourceIp: '127.0.0.1',
      detail: null,
    },
    {
      timestampUtc: '2026-10-21T12:05:00Z',
      actor: 'anonymous',
      action: 'auth.login_failed',
      outcome: 'Failure',
      sourceIp: '203.0.113.7',
      detail: 'invalid password',
    },
    {
      timestampUtc: '2026-10-21T12:10:00Z',
      actor: 'admin',
      action: 'audit.query',
      outcome: 'Success',
      sourceIp: '127.0.0.1',
      detail: 'skip=0;take=50',
    },
  ],
  totalCount: 3,
};

function stubAuditFetch(payload: unknown = auditPage) {
  return vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
    const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
    if (path.startsWith('/api/audit')) {
      return new Response(JSON.stringify(payload), { status: 200 });
    }
    throw new Error(`Unexpected fetch: ${path}`);
  });
}

describe('AuditLog', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders audit rows from the API', async () => {
    vi.stubGlobal('fetch', stubAuditFetch());

    render(<AuditLog />);

    await waitFor(() => {
      expect(screen.getByText('auth.login_failed')).toBeInTheDocument();
    });
    expect(screen.getByText('203.0.113.7')).toBeInTheDocument();
    expect(screen.getByTestId('audit-page-info')).toHaveTextContent('Showing 1–3 of 3');
  });

  it('renders mutation actions with outcome and source IP (task 6)', async () => {
    const mutationPage = {
      items: [
        {
          timestampUtc: '2026-10-23T10:00:00Z',
          actor: 'admin',
          action: 'service.mutation',
          outcome: 'Failure',
          sourceIp: '192.168.1.20',
          detail: 'unit=nginx.service; action=stop; reason=agent_unavailable',
        },
        {
          timestampUtc: '2026-10-23T10:01:00Z',
          actor: 'admin',
          action: 'service.confirmation_issued',
          outcome: 'Success',
          sourceIp: '192.168.1.20',
          detail: 'unit=nginx.service; action=stop',
        },
        {
          timestampUtc: '2026-10-23T10:02:00Z',
          actor: 'admin',
          action: 'service.mutation',
          outcome: 'Success',
          sourceIp: '192.168.1.20',
          detail: 'unit=nginx.service; action=stop; outcome=Success',
        },
        {
          timestampUtc: '2026-10-23T10:03:00Z',
          actor: 'admin',
          action: 'auth.login',
          outcome: 'Success',
          sourceIp: '127.0.0.1',
          detail: null,
        },
      ],
      totalCount: 4,
    };
    vi.stubGlobal('fetch', stubAuditFetch(mutationPage));

    render(<AuditLog />);

    await waitFor(() => {
      expect(screen.getAllByText('Service mutation').length).toBe(2);
    });
    expect(screen.getByText('Confirmation issued')).toBeInTheDocument();
    // Outcome badges and source IP are rendered for mutation rows.
    const successBadges = screen.getAllByText('Success');
    expect(successBadges.length).toBeGreaterThanOrEqual(2);
    expect(screen.getAllByText('192.168.1.20').length).toBe(3);
    // Unknown actions still render their raw value.
    expect(screen.getByText('auth.login')).toBeInTheDocument();
  });

  it('disables Previous on the first page and Next on the last page', async () => {
    vi.stubGlobal('fetch', stubAuditFetch());

    render(<AuditLog />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    });
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('requests the next page when Next is clicked', async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
      const path = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
      if (path.startsWith('/api/audit')) {
        return new Response(JSON.stringify({ items: auditPage.items, totalCount: 120 }), { status: 200 });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<AuditLog />);
    await user.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => {
      expect(screen.getByTestId('audit-page-info')).toHaveTextContent('Showing 51–53 of 120');
    });
    expect(
      fetchMock.mock.calls.filter(([input]) => String(input).includes('skip=50')).length,
    ).toBeGreaterThan(0);
  });

  it('filters rows by action text', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', stubAuditFetch());

    render(<AuditLog />);

    await waitFor(() => {
      expect(screen.getByText('auth.login')).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText('Filter by action'), 'failed');

    expect(screen.queryByText('auth.login')).not.toBeInTheDocument();
    expect(screen.getByText('auth.login_failed')).toBeInTheDocument();
  });

  it('shows an error alert when the fetch fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(async () => new Response('{}', { status: 500 })),
    );

    render(<AuditLog />);

    await waitFor(() => {
      expect(screen.getByTestId('audit-error')).toBeInTheDocument();
    });
  });
});
