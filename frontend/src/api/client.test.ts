import { ApiError, apiFetch, CSRF_HEADER_NAME, readCsrfToken } from './client';

describe('readCsrfToken', () => {
  it('returns the ha_csrf cookie value', () => {
    document.cookie = 'ha_csrf=token-123; Path=/';
    expect(readCsrfToken()).toBe('token-123');
  });

  it('returns empty string when the cookie is absent', () => {
    document.cookie = 'ha_csrf=; Max-Age=0; Path=/';
    expect(readCsrfToken()).toBe('');
  });
});

describe('apiFetch', () => {
  afterEach(() => {
    document.cookie = 'ha_csrf=; Max-Age=0; Path=/';
    vi.unstubAllGlobals();
  });

  function stubFetch(response: Response): ReturnType<typeof vi.fn> {
    const fetchMock = vi.fn().mockResolvedValue(response);
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  it('attaches the CSRF token from the cookie as a header', async () => {
    document.cookie = 'ha_csrf=abc123; Path=/';
    const fetchMock = stubFetch(new Response(JSON.stringify({ ok: true }), { status: 200 }));

    await apiFetch('/api/metrics');

    const request = fetchMock.mock.calls[0]![0] as Request;
    const headers = new Headers((fetchMock.mock.calls[0]![1] as RequestInit).headers);
    expect(headers.get(CSRF_HEADER_NAME)).toBe('abc123');
    expect(String(request)).toContain('/api/metrics');
  });

  it('sends an empty CSRF header when the cookie is missing (server fails closed)', async () => {
    const fetchMock = stubFetch(new Response(JSON.stringify({ ok: true })));

    await apiFetch('/api/metrics');

    const headers = new Headers((fetchMock.mock.calls[0]![1] as RequestInit).headers);
    expect(headers.get(CSRF_HEADER_NAME)).toBe('');
  });

  it('uses same-origin credentials so the session cookie flows', async () => {
    const fetchMock = stubFetch(new Response(JSON.stringify({ ok: true })));

    await apiFetch('/api/metrics');

    const init = fetchMock.mock.calls[0]![1] as RequestInit;
    expect(init.credentials).toBe('same-origin');
  });

  it('parses the structured error envelope into an ApiError', async () => {
    stubFetch(
      new Response(JSON.stringify({ error: { code: 'unauthorized', message: 'Sign in required.' } }), {
        status: 401,
      }),
    );

    const error = await apiFetch('/api/metrics').catch((caught) => caught);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(401);
    expect((error as ApiError).code).toBe('unauthorized');
    expect((error as ApiError).message).toBe('Sign in required.');
  });

  it('falls back to a generic error for non-JSON bodies', async () => {
    stubFetch(new Response('Bad Gateway', { status: 502 }));

    const error = await apiFetch('/api/metrics').catch((caught) => caught);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(502);
    expect((error as ApiError).code).toBe('internal_error');
  });
});
