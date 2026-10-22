import { useAuth } from './auth/useAuth';
import { AuthenticatedHome } from './screens/AuthenticatedHome';
import { Login } from './screens/Login';

export default function App() {
  const { authState, login, logout } = useAuth();

  if (authState === 'checking') {
    return (
      <main>
        <p aria-live="polite">Checking session…</p>
      </main>
    );
  }

  if (authState === 'unauthenticated') {
    return <Login onLogin={login} />;
  }

  return <AuthenticatedHome onLogout={logout} />;
}
