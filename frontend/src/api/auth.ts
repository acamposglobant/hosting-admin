import { apiFetch } from './client';

/** Login result: the API sets session + CSRF cookies on success (plan §4). */
export async function login(password: string): Promise<void> {
  await apiFetch<{ status: string }>('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({ password }),
  });
}

/** Revokes the server session and clears both cookies. */
export async function logout(): Promise<void> {
  await apiFetch<void>('/api/auth/logout', { method: 'POST' });
}

/**
 * Session probe: any session+CSRF-protected GET works as a validity check.
 * /api/metrics is used because the dashboard polls it anyway; its first access
 * per session is audited by design (plan D8) — no extra endpoint is added.
 */
export async function checkSession(): Promise<boolean> {
  try {
    await apiFetch<unknown>('/api/metrics');
    return true;
  } catch (error) {
    if (error instanceof Error && error.name === 'ApiError' && 'status' in error) {
      const status = (error as { status: number }).status;
      if (status === 401 || status === 403) {
        return false;
      }
    }
    throw error;
  }
}
