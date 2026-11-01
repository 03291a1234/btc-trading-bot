export type Provenance = 'Observed' | 'Estimated' | 'Simulated' | 'Synthetic';

export interface SystemInfo {
  name: string; version: string; execution: 'PAPER_ONLY'; liveOrderCapability: false;
  dataSource: 'Replay' | 'TradierReadOnly'; dataProvenance: Provenance; clockUtc: string; clockIsReplay: boolean;
}

export interface Selection {
  evaluatedAt: string; expiration: string; right: string; entryWindowOpen: boolean; entryWindowReason: string;
  quoteProvenance: Provenance; riskProvenance: Provenance; received: number; eligibleCount: number; summary: string;
  auditSequence: number; note: string;
  selected: null | { symbol: string; strike: number; bid: number; ask: number; spread: number; risk: { plannedLoss: number; worstCaseLoss: number } };
  rejectionCounts: Record<string, number>;
}

/** API key lives only in memory for this page; never in localStorage, URLs or source. */
export class ApiClient {
  constructor(private readonly key: string, private readonly base = '') {}

  private async request<T>(path: string, init?: RequestInit): Promise<T> {
    const r = await fetch(this.base + path, {
      ...init,
      headers: { 'X-Api-Key': this.key, 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
    });
    if (!r.ok) throw new Error(`${path}: HTTP ${r.status}`);
    return (await r.json()) as T;
  }

  system = () => this.request<SystemInfo>('/api/v8/system');
  select = (right: 'CALL' | 'PUT') => this.request<Selection>('/api/v8/options/select', { method: 'POST', body: JSON.stringify({ right }) });
}

/** Human label shown next to every number. Acceptance criterion 10. */
export function provenanceLabel(p: Provenance): string {
  switch (p) {
    case 'Observed': return 'OBSERVED (vendor data, unverified)';
    case 'Estimated': return 'ESTIMATED (model/assumption)';
    case 'Simulated': return 'SIMULATED (paper, not a fill)';
    case 'Synthetic': return 'SYNTHETIC (test data, not market data)';
  }
}

export function nonZeroRejections(counts: Record<string, number>): [string, number][] {
  return Object.entries(counts).filter(([, n]) => n > 0).sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
}
