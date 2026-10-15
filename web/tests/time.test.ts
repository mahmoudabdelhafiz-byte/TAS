import assert from 'node:assert/strict';
import test from 'node:test';
import { addDays, clock, dayLabel, duration, localDate, slotRange as range, zonedIso } from '../src/lib/time.ts';

test('local date follows the terminal zone, not the browser', () => {
  // 22:30 UTC on 19 Oct is already 20 Oct in Riyadh (UTC+3).
  assert.equal(localDate(new Date('2026-10-19T22:30:00Z'), 'Asia/Riyadh'), '2026-10-20');
  assert.equal(localDate(new Date('2026-10-19T20:30:00Z'), 'Asia/Riyadh'), '2026-10-19');
});

test('calendar arithmetic crosses months and years', () => {
  assert.equal(addDays('2026-10-31', 1), '2026-11-01');
  assert.equal(addDays('2026-01-01', -1), '2025-12-31');
});

test('slot ranges are shown in terminal time, midnight as 24:00', () => {
  const slotRange = (a: string, b: string, z: string) => range(a, b, z).replace(/[\u2066\u2069]/g, '');
  assert.equal(clock('2026-10-20T07:00:00Z', 'Asia/Riyadh'), '10:00');
  assert.equal(slotRange('2026-10-20T07:00:00+00:00', '2026-10-20T08:00:00+00:00', 'Asia/Riyadh'), '10:00–11:00');
  assert.equal(slotRange('2026-10-20T20:00:00Z', '2026-10-20T21:00:00Z', 'Asia/Riyadh'), '23:00–24:00');
});

test('slot ranges stay left-to-right inside Arabic text', () => {
  assert.equal(range('2026-10-20T07:00:00Z', '2026-10-20T08:00:00Z', 'Asia/Riyadh'), '\u206610:00–11:00\u2069');
});

test('day labels use the Gregorian calendar and Latin digits in Arabic', () => {
  assert.match(dayLabel('2026-10-20', 'en'), /Tue.*20.*Oct/);
  const ar = dayLabel('2026-10-20', 'ar');
  assert.match(ar, /20/);
  assert.doesNotMatch(ar, /[٠-٩]/);
});

test('durations', () => {
  assert.equal(duration(45), '45 min');
  assert.equal(duration(125), '2 h 05');
  assert.equal(duration(-3), '0 min');
  assert.equal(duration(90, 'دقيقة', 'ساعة'), '1 ساعة 30');
});

test('local terminal time becomes an instant with the zone offset', () => {
  assert.equal(zonedIso('2026-10-20', '10:00', 'Asia/Riyadh'), '2026-10-20T10:00:00+03:00');
  assert.equal(zonedIso('2026-07-01', '08:30', 'Europe/London'), '2026-07-01T08:30:00+01:00');
  assert.equal(zonedIso('2026-01-01', '08:30', 'Europe/London'), '2026-01-01T08:30:00+00:00');
  assert.equal(zonedIso('2026-01-01', '00:00', 'America/New_York'), '2026-01-01T00:00:00-05:00');
});
