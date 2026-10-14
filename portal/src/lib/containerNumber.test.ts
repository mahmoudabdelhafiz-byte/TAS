import { describe, expect, it } from 'vitest';
import { checkDigit, containerProblem, normalizeContainer } from './containerNumber';

describe('container numbers (ISO 6346)', () => {
  it('accepts numbers the API accepts', () => {
    for (const n of ['MSCU1234566', 'CSQU3054383', 'MAEU4001110', 'TGHU3000017', 'CMAU5100028', 'HLXU8700018']) {
      expect(containerProblem(n), n).toBeNull();
    }
  });

  it('cleans up what people type', () => {
    expect(normalizeContainer(' mscu 123456-6 ')).toBe('MSCU1234566');
    expect(containerProblem('mscu 123456-6')).toBeNull();
  });

  it('tells a typo from a wrong shape', () => {
    expect(containerProblem('MSCU1234567')).toBe('checkDigit');
    expect(containerProblem('MSCU123456')).toBe('format');
    expect(containerProblem('MSCX1234566')).toBe('format'); // category letter must be U, J or Z
    expect(containerProblem('')).toBe('format');
    expect(checkDigit('CSQU305438')).toBe(3);
  });
});
