import { useState } from 'react';
import { ApiClient, nonZeroRejections, overallHealth, provenanceLabel, type MarketHealth, type Selection, type SystemInfo } from './api';

const css = {
  page: { fontFamily: 'Inter, system-ui, sans-serif', background: '#0b0f17', color: '#d7dde8', minHeight: '100vh', padding: 24 },
  banner: { background: '#5c1f1f', color: '#ffd9d9', padding: '8px 12px', borderRadius: 6, fontWeight: 700, marginBottom: 16 },
  card: { background: '#121826', border: '1px solid #243049', borderRadius: 8, padding: 16, marginBottom: 16 },
  tag: { fontSize: 12, color: '#f5c66b' },
  button: { background: '#2a3b5f', color: '#fff', border: 0, borderRadius: 6, padding: '6px 12px', marginRight: 8, cursor: 'pointer' },
} as const;

export function App() {
  const [key, setKey] = useState('');
  const [client, setClient] = useState<ApiClient | null>(null);
  const [system, setSystem] = useState<SystemInfo | null>(null);
  const [selection, setSelection] = useState<Selection | null>(null);
  const [health, setHealth] = useState<MarketHealth | null>(null);
  const [error, setError] = useState<string | null>(null);

  const connect = async () => {
    const c = new ApiClient(key);
    try { setSystem(await c.system()); setHealth(await c.health()); setClient(c); setError(null); } catch (e) { setError(String(e)); }
  };
  const evaluate = async (right: 'CALL' | 'PUT') => {
    if (!client) return;
    try { setSelection(await client.select(right)); setHealth(await client.health()); setError(null); } catch (e) { setError(String(e)); }
  };

  return (
    <div style={css.page}>
      <div style={css.banner}>PAPER / RESEARCH ONLY — no live brokerage orders exist in this system. Not financial advice.</div>
      <h1 style={{ marginTop: 0 }}>SPX Quant Scalper Pro v8 <small style={{ fontSize: 14 }}>Phase 2</small></h1>
      {!client && (
        <div style={css.card}>
          <label>Local API key (kept in page memory only):{' '}
            <input type="password" value={key} onChange={(e) => setKey(e.target.value)} />
          </label>{' '}
          <button style={css.button} onClick={connect}>Connect</button>
        </div>
      )}
      {error && <div style={{ ...css.card, borderColor: '#a33' }}>{error}</div>}
      {system && (
        <div style={css.card}>
          <div>Execution: <b>{system.execution}</b> · live orders: <b>{String(system.liveOrderCapability)}</b></div>
          <div>Data source: <b>{system.dataSource}</b> <span style={css.tag}>{provenanceLabel(system.dataProvenance)}</span></div>
          <div>Clock: {system.clockUtc} {system.clockIsReplay && <span style={css.tag}>REPLAY CLOCK</span>}</div>
          <div style={{ marginTop: 12 }}>
            <button style={css.button} onClick={() => evaluate('CALL')}>Evaluate 0DTE CALL</button>
            <button style={css.button} onClick={() => evaluate('PUT')}>Evaluate 0DTE PUT</button>
          </div>
        </div>
      )}
      {health && (
        <div style={css.card}>
          <h3 style={{ marginTop: 0 }}>Market data — {overallHealth(health.streams)} · recorder {health.recorder.enabled ? 'ON' : 'off'}</h3>
          <table><tbody>
            {Object.entries(health.streams).map(([name, s]) => (
              <tr key={name}><td style={{ paddingRight: 16 }}>{name}</td><td style={{ paddingRight: 16 }}>{s.status}</td>
                <td style={{ paddingRight: 16 }}>{s.lastSuccess ?? '—'}</td><td>{s.lastError ?? ''}</td></tr>
            ))}
          </tbody></table>
        </div>
      )}
      {selection && (
        <div style={css.card}>
          <h3 style={{ marginTop: 0 }}>Contract selection — {selection.right} {selection.expiration}</h3>
          <div style={css.tag}>Quotes: {provenanceLabel(selection.quoteProvenance)} · Risk: {provenanceLabel(selection.riskProvenance)} · Greeks: {selection.greeksSource}{selection.estimatedGreeksCount ? ` (${selection.estimatedGreeksCount} ESTIMATED)` : ''}</div>
          <p>{selection.summary}</p>
          <p>Entry window: {selection.entryWindowOpen ? 'open' : `closed (${selection.entryWindowReason})`} · audit #{selection.auditSequence}</p>
          {selection.selected && (
            <p>Selected {selection.selected.symbol}: bid {selection.selected.bid} / ask {selection.selected.ask} · planned loss ${selection.selected.risk.plannedLoss} · worst case ${selection.selected.risk.worstCaseLoss}</p>
          )}
          <table><tbody>
            {nonZeroRejections(selection.rejectionCounts).map(([reason, n]) => (
              <tr key={reason}><td style={{ paddingRight: 16 }}>{reason}</td><td>{n}</td></tr>
            ))}
          </tbody></table>
          <p style={{ fontSize: 12 }}>{selection.note}</p>
        </div>
      )}
    </div>
  );
}
