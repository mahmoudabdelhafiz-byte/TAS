/**
 * ISO 6346 container numbers: owner code (3 letters) + category (U, J or Z) + 6-digit serial + check digit.
 * Same rules as the API (ContainerNumber.cs), so a typo shows while typing instead of after booking.
 */

const letterValues: Record<string, number> = (() => {
  const values: Record<string, number> = {};
  let value = 10;
  for (const letter of 'ABCDEFGHIJKLMNOPQRSTUVWXYZ') {
    if (value % 11 === 0) value++; // 11, 22, 33 are skipped
    values[letter] = value++;
  }
  return values;
})();

/** Upper case, letters and digits only (like the API): "mscu 123456-6" → "MSCU1234566". */
export function normalizeContainer(input: string): string {
  return input.toUpperCase().replace(/[^A-Z0-9]/g, '');
}

export function checkDigit(first10: string): number {
  let sum = 0;
  for (let i = 0; i < 10; i++) {
    const ch = first10[i]!;
    const value = /[A-Z]/.test(ch) ? letterValues[ch]! : Number(ch);
    sum += value * 2 ** i;
  }
  return (sum % 11) % 10;
}

export type ContainerProblem = 'format' | 'checkDigit' | null;

/** null when valid; 'format' for a wrong shape; 'checkDigit' when the last digit doesn't match (a typo). */
export function containerProblem(input: string): ContainerProblem {
  const value = normalizeContainer(input);
  if (!/^[A-Z]{3}[UJZ]\d{7}$/.test(value)) return 'format';
  return checkDigit(value.slice(0, 10)) === Number(value[10]) ? null : 'checkDigit';
}
