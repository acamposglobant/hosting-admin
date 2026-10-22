import { formatBytes, formatTimestamp, formatUptime } from './format';

describe('formatBytes', () => {
  it('formats bytes without decimals', () => {
    expect(formatBytes(512)).toBe('512 B');
  });

  it('formats KiB with one decimal', () => {
    expect(formatBytes(2048)).toBe('2.0 KiB');
  });

  it('formats GiB', () => {
    expect(formatBytes(2 * 1024 ** 3)).toBe('2.0 GiB');
  });

  it('uses no decimals for large values', () => {
    expect(formatBytes(150 * 1024 ** 3)).toBe('150 GiB');
  });

  it('returns a dash for null, undefined, NaN and negatives', () => {
    expect(formatBytes(null)).toBe('—');
    expect(formatBytes(undefined)).toBe('—');
    expect(formatBytes(Number.NaN)).toBe('—');
    expect(formatBytes(-5)).toBe('—');
  });
});

describe('formatUptime', () => {
  it('formats days and hours', () => {
    expect(formatUptime(864000)).toBe('10 d 0 h');
  });

  it('formats hours and minutes', () => {
    expect(formatUptime(3 * 3600 + 5 * 60)).toBe('3 h 5 min');
  });

  it('formats minutes only', () => {
    expect(formatUptime(120)).toBe('2 min');
  });

  it('returns a dash for null or invalid input', () => {
    expect(formatUptime(null)).toBe('—');
    expect(formatUptime(-1)).toBe('—');
  });
});

describe('formatTimestamp', () => {
  it('formats an ISO timestamp as UTC', () => {
    expect(formatTimestamp('2026-10-21T12:34:56Z')).toBe('2026-10-21 12:34:56 UTC');
  });

  it('returns the input unchanged when unparseable', () => {
    expect(formatTimestamp('not-a-date')).toBe('not-a-date');
  });
});
