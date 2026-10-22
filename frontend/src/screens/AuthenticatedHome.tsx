/**
 * Minimal authenticated view (plan task 14). Dashboard and audit screens are
 * later tasks; this placeholder proves the auth flow end to end.
 */
export function AuthenticatedHome({ onLogout }: { onLogout: () => Promise<void> }) {
  return (
    <main>
      <h1>Hosting Admin</h1>
      <p>Signed in.</p>
      <button onClick={() => void onLogout()}>Sign out</button>
    </main>
  );
}
