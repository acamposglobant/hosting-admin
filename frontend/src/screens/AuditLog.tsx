import { useState } from 'react';
import { fetchAuditPage } from '../api/types';
import { usePolling } from '../hooks/usePolling';
import { formatTimestamp } from '../lib/format';

/** Page size for the audit table (server cap is 200, plan §4). */
const AUDIT_PAGE_SIZE = 50;

/**
 * Audit log screen (plan §5, screen 3): paged table with a client-side action
 * filter over the loaded page. Pagination is server-side (skip/take); the
 * view itself is audited server-side on every fetch (plan §3.7).
 */
export function AuditLog() {
  const [skip, setSkip] = useState(0);
  const [actionFilter, setActionFilter] = useState('');

  const page = usePolling(() => fetchAuditPage(skip, AUDIT_PAGE_SIZE), 30_000, skip);

  const filterText = actionFilter.trim().toLowerCase();
  const filteredItems =
    page.data?.items.filter((item) => item.action.toLowerCase().includes(filterText)) ?? [];

  const canGoPrevious = skip > 0;
  const canGoNext = page.data !== null && skip + AUDIT_PAGE_SIZE < page.data.totalCount;

  return (
    <main aria-label="Audit log">
      <h1>Audit log</h1>

      {page.error && (
        <p role="alert" data-testid="audit-error">
          {page.error}
        </p>
      )}

      <div className="audit-controls">
        <label htmlFor="action-filter">Filter by action</label>
        <input
          id="action-filter"
          type="search"
          value={actionFilter}
          onChange={(event) => setActionFilter(event.target.value)}
          placeholder="e.g. auth.login"
        />
      </div>

      <table>
        <thead>
          <tr>
            <th scope="col">Timestamp</th>
            <th scope="col">Actor</th>
            <th scope="col">Action</th>
            <th scope="col">Outcome</th>
            <th scope="col">Source IP</th>
            <th scope="col">Detail</th>
          </tr>
        </thead>
        <tbody>
          {filteredItems.map((item) => (
            <tr key={`${item.timestampUtc}-${item.action}-${item.detail ?? ''}`}>
              <td>{formatTimestamp(item.timestampUtc)}</td>
              <td>{item.actor}</td>
              <td>{item.action}</td>
              <td>
                <span className={`badge badge-${item.outcome.toLowerCase()}`}>{item.outcome}</span>
              </td>
              <td>{item.sourceIp ?? '—'}</td>
              <td>{item.detail ?? '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <div className="audit-pagination">
        <button onClick={() => setSkip(Math.max(0, skip - AUDIT_PAGE_SIZE))} disabled={!canGoPrevious}>
          Previous
        </button>
        <span data-testid="audit-page-info">
          {page.data === null
            ? 'Loading…'
            : `Showing ${skip + 1}–${skip + page.data.items.length} of ${page.data.totalCount}`}
        </span>
        <button onClick={() => setSkip(skip + AUDIT_PAGE_SIZE)} disabled={!canGoNext}>
          Next
        </button>
      </div>
    </main>
  );
}
