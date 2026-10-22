/**
 * CSRF cookie name and header name. These mirror the server configuration
 * (Security:CsrfCookieName / Security:CsrfHeaderName, plan D4). The CSRF
 * cookie is intentionally not HttpOnly: the SPA reads it and echoes it in
 * the header on every cookie-authenticated request (double-submit pattern).
 */
export const CSRF_COOKIE_NAME = 'ha_csrf';
export const CSRF_HEADER_NAME = 'X-CSRF-Token';

/** Structured API error (plan §3.7): code + generic message, no internals. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

/** Reads the double-submit CSRF token from the (non-HttpOnly) cookie. */
export function readCsrfToken(): string {
  const prefix = `${CSRF_COOKIE_NAME}=`;
  for (const part of document.cookie.split(';')) {
    const trimmed = part.trim();
    if (trimmed.startsWith(prefix)) {
      return decodeURIComponent(trimmed.slice(prefix.length));
    }
  }
  return '';
}

/**
 * Fetch wrapper for all API calls (plan §5): attaches the CSRF token to every
 * request (the server requires it whenever the session cookie is presented),
 * sends credentials same-origin, and parses the structured error envelope.
 */
export async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set(CSRF_HEADER_NAME, readCsrfToken());
  if (init.body !== undefined && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }

  const response = await fetch(path, {
    ...init,
    headers,
    credentials: 'same-origin',
  });

  if (!response.ok) {
    throw await toApiError(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

/** Parses the server's error envelope without leaking unexpected internals. */
async function toApiError(response: Response): Promise<ApiError> {
  let code = 'internal_error';
  let message = 'Request failed.';
  try {
    const body = (await response.json()) as { error?: { code?: string; message?: string } };
    if (typeof body?.error?.code === 'string') {
      code = body.error.code;
    }
    if (typeof body?.error?.message === 'string') {
      message = body.error.message;
    }
  } catch {
    // Non-JSON error body: keep the generic defaults.
  }
  return new ApiError(response.status, code, message);
}
