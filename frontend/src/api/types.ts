import { apiFetch } from './client';

/**
 * API response contracts (plan §4). Field names mirror the API's camelCase
 * JSON serialization; nullable fields map to the server's "unavailable" shape
 * (D11): isAvailable=false with a reason instead of an error.
 */

export interface CpuMetrics {
  usagePercent: number | null;
  cores: number | null;
  loadAverage1m: number | null;
  loadAverage5m: number | null;
  loadAverage15m: number | null;
  sampledAt: string;
}

export interface MemoryMetrics {
  totalBytes: number | null;
  usedBytes: number | null;
  availableBytes: number | null;
  usagePercent: number | null;
}

export interface SwapMetrics {
  totalBytes: number | null;
  usedBytes: number | null;
  usagePercent: number | null;
}

export interface MetricsSnapshot {
  isAvailable: boolean;
  unavailableReason: string | null;
  cpu: CpuMetrics | null;
  memory: MemoryMetrics | null;
  swap: SwapMetrics | null;
  uptimeSeconds: number | null;
}

export interface SystemInfoSnapshot {
  isAvailable: boolean;
  unavailableReason: string | null;
  hostname: string | null;
  operatingSystem: string | null;
  kernelVersion: string | null;
  architecture: string | null;
}

export interface DiskUsage {
  device: string;
  mountPoint: string;
  fileSystem: string;
  totalBytes: number;
  usedBytes: number;
  availableBytes: number;
  usagePercent: number;
}

export interface DiskSnapshot {
  isAvailable: boolean;
  unavailableReason: string | null;
  disks: DiskUsage[];
}

export interface ServiceStatus {
  name: string;
  loadState: string;
  activeState: string;
  subState: string;
  enabledState: string;
  since: string | null;
}

export interface ServicesSnapshot {
  services: ServiceStatus[];
  capabilityAvailable: boolean;
}

export interface AuditEventDto {
  timestampUtc: string;
  actor: string;
  action: string;
  outcome: string;
  sourceIp: string | null;
  detail: string | null;
}

export interface AuditPageDto {
  items: AuditEventDto[];
  totalCount: number;
}

export function fetchMetrics(): Promise<MetricsSnapshot> {
  return apiFetch<MetricsSnapshot>('/api/metrics');
}

export function fetchSystemInfo(): Promise<SystemInfoSnapshot> {
  return apiFetch<SystemInfoSnapshot>('/api/system/info');
}

export function fetchDisks(): Promise<DiskSnapshot> {
  return apiFetch<DiskSnapshot>('/api/disks');
}

export function fetchServices(): Promise<ServicesSnapshot> {
  return apiFetch<ServicesSnapshot>('/api/services');
}

export function fetchAuditPage(skip: number, take: number): Promise<AuditPageDto> {
  return apiFetch<AuditPageDto>(`/api/audit?skip=${encodeURIComponent(skip)}&take=${encodeURIComponent(take)}`);
}
