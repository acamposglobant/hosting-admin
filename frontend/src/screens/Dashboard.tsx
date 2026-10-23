import type { ReactNode } from 'react';
import { fetchDisks, fetchMetrics, fetchServices, fetchSystemInfo } from '../api/types';
import { usePolling } from '../hooks/usePolling';
import { formatBytes, formatUptime } from '../lib/format';
import { ServiceActions } from './ServiceActions';

/** Dashboard polling interval (plan D10): 10 seconds. */
export const DASHBOARD_POLL_INTERVAL_MS = 10_000;

function UnavailableBanner({ reason }: { reason: string | null }) {
  return (
    <div role="status" className="banner banner-warning" data-testid="unavailable-banner">
      Data unavailable{reason ? `: ${reason}` : ''}.
    </div>
  );
}

function MetricCard({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="card" aria-label={title}>
      <h2>{title}</h2>
      {children}
    </section>
  );
}

function UsageBar({ percent }: { percent: number | null }) {
  if (percent === null) {
    return <span>—</span>;
  }
  const clamped = Math.min(100, Math.max(0, percent));
  return (
    <div className="usage-bar" role="meter" aria-valuenow={clamped} aria-valuemin={0} aria-valuemax={100}>
      <div className="usage-bar-fill" style={{ width: `${clamped}%` }} />
      <span>{percent.toFixed(1)}%</span>
    </div>
  );
}

/**
 * Dashboard screen (plan §5, screen 2): CPU, memory/swap, uptime, OS info,
 * disks, services — polled every 10 s (D10). Capability problems render as
 * structured banners (D11), never as a crashed page.
 */
export function Dashboard() {
  const metrics = usePolling(fetchMetrics, DASHBOARD_POLL_INTERVAL_MS);
  const systemInfo = usePolling(fetchSystemInfo, DASHBOARD_POLL_INTERVAL_MS);
  const disks = usePolling(fetchDisks, DASHBOARD_POLL_INTERVAL_MS);
  const services = usePolling(fetchServices, DASHBOARD_POLL_INTERVAL_MS);

  const anyLoading = metrics.loading || systemInfo.loading || disks.loading || services.loading;

  return (
    <main aria-label="Dashboard">
      <h1>Dashboard</h1>
      {anyLoading && <p aria-live="polite">Loading…</p>}

      {metrics.error && (
        <p role="alert" data-testid="metrics-error">
          {metrics.error}
        </p>
      )}
      {systemInfo.error && (
        <p role="alert" data-testid="system-info-error">
          {systemInfo.error}
        </p>
      )}
      {disks.error && (
        <p role="alert" data-testid="disks-error">
          {disks.error}
        </p>
      )}
      {services.error && (
        <p role="alert" data-testid="services-error">
          {services.error}
        </p>
      )}

      <section aria-label="System information" className="card">
        <h2>System</h2>
        {systemInfo.data === null ? null : systemInfo.data.isAvailable ? (
          <dl>
            <dt>Hostname</dt>
            <dd>{systemInfo.data.hostname ?? '—'}</dd>
            <dt>Operating system</dt>
            <dd>{systemInfo.data.operatingSystem ?? '—'}</dd>
            <dt>Kernel</dt>
            <dd>{systemInfo.data.kernelVersion ?? '—'}</dd>
            <dt>Architecture</dt>
            <dd>{systemInfo.data.architecture ?? '—'}</dd>
          </dl>
        ) : (
          <UnavailableBanner reason={systemInfo.data.unavailableReason} />
        )}
      </section>

      <section aria-label="CPU and memory" className="card-grid">
        <MetricCard title="CPU">
          {metrics.data === null ? null : !metrics.data.isAvailable ? (
            <UnavailableBanner reason={metrics.data.unavailableReason} />
          ) : (
            <>
              <p data-testid="cpu-usage">
                {metrics.data.cpu?.usagePercent !== null && metrics.data.cpu?.usagePercent !== undefined
                  ? `${metrics.data.cpu.usagePercent.toFixed(1)}%`
                  : '—'}
              </p>
              <p>
                {metrics.data.cpu?.cores !== null && metrics.data.cpu?.cores !== undefined
                  ? `${metrics.data.cpu.cores} cores`
                  : 'cores: —'}
              </p>
              <p>
                load {metrics.data.cpu?.loadAverage1m?.toFixed(2) ?? '—'} /{' '}
                {metrics.data.cpu?.loadAverage5m?.toFixed(2) ?? '—'} /{' '}
                {metrics.data.cpu?.loadAverage15m?.toFixed(2) ?? '—'}
              </p>
            </>
          )}
        </MetricCard>

        <MetricCard title="Memory">
          {metrics.data === null ? null : !metrics.data.isAvailable || metrics.data.memory === null ? (
            <UnavailableBanner reason={metrics.data.unavailableReason} />
          ) : (
            <>
              <p data-testid="memory-usage">
                {formatBytes(metrics.data.memory.usedBytes)} / {formatBytes(metrics.data.memory.totalBytes)}
              </p>
              <UsageBar percent={metrics.data.memory.usagePercent} />
            </>
          )}
        </MetricCard>

        <MetricCard title="Swap">
          {metrics.data === null ? null : !metrics.data.isAvailable || metrics.data.swap === null ? (
            <UnavailableBanner reason={metrics.data.unavailableReason} />
          ) : (
            <>
              <p>{formatBytes(metrics.data.swap.usedBytes)} / {formatBytes(metrics.data.swap.totalBytes)}</p>
              <UsageBar percent={metrics.data.swap.usagePercent} />
            </>
          )}
        </MetricCard>

        <MetricCard title="Uptime">
          <p data-testid="uptime">{formatUptime(metrics.data?.uptimeSeconds)}</p>
        </MetricCard>
      </section>

      <section aria-label="Disks" className="card">
        <h2>Disks</h2>
        {disks.data === null ? null : !disks.data.isAvailable ? (
          <UnavailableBanner reason={disks.data.unavailableReason} />
        ) : (
          <table>
            <thead>
              <tr>
                <th scope="col">Device</th>
                <th scope="col">Mount</th>
                <th scope="col">Filesystem</th>
                <th scope="col">Usage</th>
              </tr>
            </thead>
            <tbody>
              {disks.data.disks.map((disk) => (
                <tr key={disk.mountPoint}>
                  <td>{disk.device}</td>
                  <td>{disk.mountPoint}</td>
                  <td>{disk.fileSystem}</td>
                  <td>
                    {formatBytes(disk.usedBytes)} / {formatBytes(disk.totalBytes)}{' '}
                    ({disk.usagePercent.toFixed(1)}%)
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section aria-label="Services" className="card">
        <h2>Services</h2>
        {services.data === null ? null : !services.data.capabilityAvailable ? (
          <div role="status" className="banner banner-warning" data-testid="capability-banner">
            systemd status is not available on this host.
          </div>
        ) : (
          <table>
            <thead>
              <tr>
                <th scope="col">Unit</th>
                <th scope="col">Active</th>
                <th scope="col">Sub</th>
                <th scope="col">Enabled</th>
                <th scope="col">Since</th>
                {services.data.mutationCapabilityAvailable && <th scope="col">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {services.data.services.map((service) => (
                <tr key={service.name}>
                  <td>{service.name}</td>
                  <td>
                    <span className={`badge badge-${service.activeState}`}>{service.activeState}</span>
                  </td>
                  <td>{service.subState}</td>
                  <td>{service.enabledState}</td>
                  <td>{service.since === null ? '—' : service.since}</td>
                  {services.data!.mutationCapabilityAvailable && (
                    <td>
                      <ServiceActions
                        service={service}
                        mutationCapabilityAvailable={services.data!.mutationCapabilityAvailable}
                        onActionCompleted={() => void services.refresh()}
                      />
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </main>
  );
}

