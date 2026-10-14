import { describe, expect, it } from 'vitest';
import { ar } from './ar';
import { en, type MessageKey } from './en';
import { format, locale, translate } from './I18n';

const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

describe('translations', () => {
  it('Arabic has every English text, none empty', () => {
    for (const key of Object.keys(en) as MessageKey[]) {
      expect(ar[key], key).toBeTruthy();
    }
    expect(Object.keys(ar).sort()).toEqual(Object.keys(en).sort());
  });

  it('Arabic and English use the same placeholders', () => {
    for (const key of Object.keys(en) as MessageKey[]) {
      expect(placeholders(ar[key]), key).toEqual(placeholders(en[key]));
    }
  });

  it('fills placeholders and leaves unknown ones visible', () => {
    expect(format('Hello {name}, {missing}', { name: 'Khalid' })).toBe('Hello Khalid, {missing}');
    expect(translate('ar', 'invitationSent', { email: 'a@b.example' })).toBe('أُرسلت الدعوة إلى a@b.example.');
  });

  it('formats Arabic dates with the Gregorian calendar and Latin digits', () => {
    const text = new Intl.DateTimeFormat(locale('ar'), { timeZone: 'Asia/Riyadh', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(
      new Date('2026-10-20T07:30:00Z'),
    );
    expect(text).toContain('10:30');
    expect(text).not.toMatch(/[٠-٩]/);
  });
});
