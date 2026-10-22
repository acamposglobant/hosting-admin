import { useCallback, useEffect, useState } from 'react';
import { checkSession, login as loginRequest, logout as logoutRequest } from '../api/auth';

export type AuthState = 'checking' | 'unauthenticated' | 'authenticated';

/**
 * Auth flow (plan task 14): probe the session on mount, expose login/logout.
 * The probe is a real authenticated GET — 401/403 means "show the login form".
 */
export function useAuth(): {
  authState: AuthState;
  login: (password: string) => Promise<void>;
  logout: () => Promise<void>;
} {
  const [authState, setAuthState] = useState<AuthState>('checking');

  useEffect(() => {
    let cancelled = false;
    checkSession()
      .then((authenticated) => {
        if (!cancelled) {
          setAuthState(authenticated ? 'authenticated' : 'unauthenticated');
        }
      })
      .catch(() => {
        // Probe failure (API down, network): fail closed to the login form.
        if (!cancelled) {
          setAuthState('unauthenticated');
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const login = useCallback(async (password: string) => {
    await loginRequest(password);
    setAuthState('authenticated');
  }, []);

  const logout = useCallback(async () => {
    await logoutRequest();
    setAuthState('unauthenticated');
  }, []);

  return { authState, login, logout };
}
