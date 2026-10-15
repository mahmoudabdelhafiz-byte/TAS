// Dates and times are always shown in the terminal's time zone, whatever the browser's zone is.
// The API speaks UTC instants (ISO strings) and local calendar dates ("2026-10-20").

export type Lang = 'en' | 'ar';

/** Gregorian calendar and Latin digits in Arabic too: plates, container numbers and times read the same at the gate. */
export function locale(lang: Lang): string {
  return lang === 'ar' ? 'ar-u-ca-gregory-nu-latn' : 'en-GB';
}

/** The local calendar date ("YYYY-MM-DD") at an instant in a time zone. */
export function localDate(instant: Date, timeZone: string): string {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' })
    .formatToParts(instant);
  const get = (type: string) => parts.find(p => p.type === type)?.value ?? '';
  return `${get('year')}-${get('month')}-${get('day')}`;
}

/** Calendar arithmetic on "YYYY-MM-DD" strings (no time zone involved). */
export function addDays(date: string, days: number): string {
  const [y, m, d] = date.split('-').map(Number);
  const utc = new Date(Date.UTC(y, m - 1, d + days));
  return utc.toISOString().slice(0, 10);
}

/** The instant of a local date and time in a time zone, as ISO 8601 with offset ("2026-10-20T10:00:00+03:00"). */
export function zonedIso(date: string, time: string, timeZone: string): string {
  const [y, mo, d] = date.split('-').map(Number);
  const [h, mi] = time.split(':').map(Number);
  const wall = Date.UTC(y, mo - 1, d, h, mi);
  // The zone's offset at that moment: format a UTC guess in the zone and compare (twice, for DST edges).
  const offsetAt = (utc: number) => {
    const parts = new Intl.DateTimeFormat('en-US', {
      timeZone, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit',
    }).formatToParts(new Date(utc));
    const get = (t: string) => Number(parts.find(p => p.type === t)?.value);
    return (Date.UTC(get('year'), get('month') - 1, get('day'), get('hour') % 24, get('minute')) - utc) / 60000;
  };
  let offset = offsetAt(wall);
  offset = offsetAt(wall - offset * 60000);
  const sign = offset >= 0 ? '+' : '-';
  const abs = Math.abs(offset);
  return `${date}T${time.length === 5 ? time : time.padStart(5, '0')}:00${sign}${String(Math.floor(abs / 60)).padStart(2, '0')}:${String(abs % 60).padStart(2, '0')}`;
}

/** "10:00" in the terminal's zone. */
export function clock(iso: string, timeZone: string): string {
  return new Intl.DateTimeFormat('en-GB', { timeZone, hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
    .format(new Date(iso));
}

/** "10:00–11:00", wrapped in a left-to-right isolate so it never reads backwards inside Arabic text. */
export function slotRange(startIso: string, endIso: string, timeZone: string): string {
  const end = clock(endIso, timeZone);
  return `\u2066${clock(startIso, timeZone)}–${end === '00:00' ? '24:00' : end}\u2069`;
}

/** "Tue 20 Oct" / "الثلاثاء 20 أكتوبر" for a local date string. */
export function dayLabel(date: string, lang: Lang): string {
  const [y, m, d] = date.split('-').map(Number);
  return new Intl.DateTimeFormat(locale(lang), { timeZone: 'UTC', weekday: 'short', day: 'numeric', month: 'short' })
    .format(new Date(Date.UTC(y, m - 1, d)));
}

/** "20 Oct, 10:42" in the terminal's zone. */
export function stamp(iso: string, timeZone: string, lang: Lang): string {
  return new Intl.DateTimeFormat(locale(lang), {
    timeZone, day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).format(new Date(iso));
}

/** Minutes as "45 min" or "2 h 05" (unit words passed in so Arabic can use its own). */
export function duration(minutes: number, min = 'min', hour = 'h'): string {
  const m = Math.max(0, Math.round(minutes));
  if (m < 60) return `${m} ${min}`;
  return `${Math.floor(m / 60)} ${hour} ${String(m % 60).padStart(2, '0')}`;
}
