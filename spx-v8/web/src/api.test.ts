import { describe, expect, it } from 'vitest';
import { nonZeroRejections, provenanceLabel } from './api';

describe('provenance labels', () => {
  it('never presents synthetic or simulated data as observed', () => {
    expect(provenanceLabel('Synthetic')).toContain('not market data');
    expect(provenanceLabel('Simulated')).toContain('not a fill');
    expect(provenanceLabel('Estimated')).toContain('ESTIMATED');
  });
});

describe('rejection summary', () => {
  it('drops zero counts and sorts descending then by name', () => {
    expect(nonZeroRejections({ A: 0, SpreadTooWide: 2, DeltaOutOfRange: 5, B: 2 })).toEqual([
      ['DeltaOutOfRange', 5], ['B', 2], ['SpreadTooWide', 2],
    ]);
  });
});
