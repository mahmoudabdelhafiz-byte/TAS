// Fees: amounts arrive in the currency's smallest unit (2500 = SAR 25.00); shown with the currency code and Latin digits.

import { locale, type Lang } from './time.ts';

export interface FeeSchedule {
  currency: string;
  decimals: number;
  booking: number;
  noShow: number;
  lateCancel: number;
  lateCancelHours: number;
}

/** "SAR 25.00" (Arabic: "25.00 SAR" in Arabic order). */
export function money(minor: number, currency: string, decimals: number, lang: Lang): string {
  return new Intl.NumberFormat(locale(lang), {
    style: 'currency', currency, currencyDisplay: 'code', minimumFractionDigits: decimals, maximumFractionDigits: decimals,
  }).format(minor / 10 ** decimals);
}

/** The smallest unit from a typed amount ("25", "25.5" → 2500, 2550); null when it isn't an amount. */
export function toMinor(text: string, decimals: number): number | null {
  const clean = text.trim().replace(',', '.');
  if (clean === '') return 0;
  if (!/^\d+(\.\d+)?$/.test(clean)) return null;
  const [whole, fraction = ''] = clean.split('.');
  if (fraction.length > decimals) return null;
  return Number(whole) * 10 ** decimals + Number(fraction.padEnd(decimals, '0') || '0');
}

export const fromMinor = (minor: number, decimals: number): string => (minor / 10 ** decimals).toFixed(decimals);
