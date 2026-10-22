import { useState, type FormEvent } from 'react';
import { ApiError } from '../api/client';

/**
 * Login screen (plan §5, screen 1): credentials form with a generic error on
 * failure — the API deliberately does not distinguish wrong password from
 * missing credential (plan D7), and neither does this message.
 */
export function Login({ onLogin }: { onLogin: (password: string) => Promise<void> }) {
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending) {
      return;
    }
    setError(null);
    setPending(true);
    try {
      await onLogin(password);
    } catch (submitError) {
      setError(
        submitError instanceof ApiError && submitError.status === 429
          ? 'Too many attempts. Try again later.'
          : 'Login failed. Check the password and try again.',
      );
    } finally {
      setPending(false);
    }
  }

  return (
    <main>
      <h1>Hosting Admin</h1>
      <form onSubmit={handleSubmit} aria-label="Login form">
        <label htmlFor="password">Password</label>
        <input
          id="password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          disabled={pending}
          required
        />
        <button type="submit" disabled={pending || password.length === 0}>
          {pending ? 'Signing in…' : 'Sign in'}
        </button>
        {error !== null && (
          <p role="alert" data-testid="login-error">
            {error}
          </p>
        )}
      </form>
    </main>
  );
}
