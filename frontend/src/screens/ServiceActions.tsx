import { useState } from 'react';
import {
  cancelServiceAction,
  confirmServiceAction,
  isServiceActionPending,
  requestServiceAction,
  type ServiceActionRequestDto,
  type ServiceStatus,
} from '../api/types';

/** How long a pending confirmation stays valid (mirrors the server TTL, D20). */
const CONFIRMATION_TTL_MS = 5 * 60 * 1000;

type ServiceAction = 'start' | 'stop' | 'restart';

interface PendingConfirmation {
  confirmationId: string;
  unit: string;
  action: ServiceAction;
  expiresAt: string;
}

interface ServiceActionsProps {
  service: ServiceStatus;
  mutationCapabilityAvailable: boolean;
  /** Called after a successful mutation so the parent can refresh status. */
  onActionCompleted: () => void;
}

/**
 * Per-service action controls (plan task 5): start runs directly; stop and
 * restart are destructive (D20) and require a two-step confirmation. All
 * buttons are hidden when the mutation capability is unavailable (D24) and
 * disabled while a request is in flight.
 */
export function ServiceActions({ service, mutationCapabilityAvailable, onActionCompleted }: ServiceActionsProps) {
  const [pending, setPending] = useState<PendingConfirmation | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null);

  if (!mutationCapabilityAvailable) {
    return null;
  }

  function clearMessage() {
    setMessage(null);
  }

  async function runDirect(action: ServiceAction) {
    setBusy(true);
    clearMessage();
    try {
      const result: ServiceActionRequestDto = await requestServiceAction(service.name, action);
      if (isServiceActionPending(result)) {
        // Defensive: the server only returns a pending confirmation for
        // stop/restart, which never reach this direct path.
        setPending({
          confirmationId: result.confirmationId,
          unit: result.unit,
          action: result.action as ServiceAction,
          expiresAt: result.expiresAt,
        });
        return;
      }
      setMessage({ kind: 'success', text: `${service.name} ${action} completed.` });
      onActionCompleted();
    } catch {
      setMessage({ kind: 'error', text: `${action} failed. Please try again.` });
    } finally {
      setBusy(false);
    }
  }

  async function runDestructive(action: ServiceAction) {
    setBusy(true);
    clearMessage();
    try {
      const result = await requestServiceAction(service.name, action);
      if (!isServiceActionPending(result)) {
        // Server executed directly (unexpected for stop/restart) — treat as done.
        setMessage({ kind: 'success', text: `${service.name} ${action} completed.` });
        onActionCompleted();
        return;
      }
      setPending({
        confirmationId: result.confirmationId,
        unit: result.unit,
        action: result.action as ServiceAction,
        expiresAt: result.expiresAt,
      });
    } catch {
      setMessage({ kind: 'error', text: `${action} failed. Please try again.` });
    } finally {
      setBusy(false);
    }
  }

  async function handleConfirm() {
    if (pending === null) {
      return;
    }
    setBusy(true);
    clearMessage();
    try {
      await confirmServiceAction(pending.confirmationId);
      setPending(null);
      setMessage({ kind: 'success', text: `${pending.unit} ${pending.action} completed.` });
      onActionCompleted();
    } catch {
      setMessage({ kind: 'error', text: 'The confirmation is no longer valid. Please try again.' });
      setPending(null);
    } finally {
      setBusy(false);
    }
  }

  async function handleCancel() {
    if (pending === null) {
      return;
    }
    const confirmationId = pending.confirmationId;
    setPending(null);
    setBusy(true);
    clearMessage();
    try {
      await cancelServiceAction(confirmationId);
    } catch {
      // A failed cancel is harmless: the confirmation expires server-side
      // after the TTL. Surface a generic note without blocking the UI.
      setMessage({ kind: 'error', text: 'Could not cancel; the request will expire on its own.' });
    } finally {
      setBusy(false);
    }
  }

  const confirmationExpired = pending !== null && Date.parse(pending.expiresAt) + CONFIRMATION_TTL_MS < Date.now();

  return (
    <div className="service-actions" data-testid={`service-actions-${service.name}`}>
      <button
        type="button"
        className="action-button"
        disabled={busy}
        aria-label={`Start ${service.name}`}
        onClick={() => void runDirect('start')}
      >
        Start
      </button>
      <button
        type="button"
        className="service-action-button"
        disabled={busy}
        aria-label={`Stop ${service.name}`}
        onClick={() => void runDestructive('stop')}
      >
        Stop
      </button>
      <button
        type="button"
        className="service-action-button"
        disabled={busy}
        aria-label={`Restart ${service.name}`}
        onClick={() => void runDestructive('restart')}
      >
        Restart
      </button>

      {pending !== null && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label={`Confirm ${pending.action} ${pending.unit}`}
          className="confirmation-dialog"
          data-testid="confirmation-dialog"
        >
          <p>
            Are you sure you want to {pending.action} <strong>{pending.unit}</strong>? This may interrupt the service.
          </p>
          {confirmationExpired && (
            <p role="alert" data-testid="confirmation-expired">
              This confirmation has expired. Cancel and try again.
            </p>
          )}
          <div className="confirmation-actions">
            <button
              type="button"
              className="service-action-button service-action-button-danger"
              disabled={busy || confirmationExpired}
              onClick={() => void handleConfirm()}
            >
              Confirm {pending.action}
            </button>
            <button type="button" className="service-action-button" disabled={busy} onClick={() => void handleCancel()}>
              Cancel
            </button>
          </div>
        </div>
      )}

      {message !== null && (
        <p role={message.kind === 'error' ? 'alert' : 'status'} data-testid={`action-message-${service.name}`}>
          {message.text}
        </p>
      )}
    </div>
  );
}
