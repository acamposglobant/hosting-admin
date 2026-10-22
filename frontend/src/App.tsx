import { useState } from 'react';
import { useAuth } from './auth/useAuth';
import { AuditLog } from './screens/AuditLog';
import { Dashboard } from './screens/Dashboard';
import { Login } from './screens/Login';

type Screen = 'dashboard' | 'audit';

export default function App() {
  const { authState, login, logout } = useAuth();
  const [screen, setScreen] = useState<Screen>('dashboard');

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

  return (
    <>
      <nav aria-label="Main navigation">
        <button onClick={() => setScreen('dashboard')} disabled={screen === 'dashboard'}>
          Dashboard
        </button>
        <button onClick={() => setScreen('audit')} disabled={screen === 'audit'}>
          Audit log
        </button>
        <button onClick={() => void logout()}>Sign out</button>
      </nav>
      {screen === 'dashboard' ? <Dashboard /> : <AuditLog />}
    </>
  );
}
