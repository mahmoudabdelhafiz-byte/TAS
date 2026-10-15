import assert from 'node:assert/strict';
import test from 'node:test';
import { ar, en, translate, translateDynamic, type TextKey } from '../src/lib/i18n.ts';

test('Arabic has every English text and nothing extra, with the same placeholders', () => {
  assert.deepEqual(Object.keys(ar).sort(), Object.keys(en).sort());
  for (const key of Object.keys(en) as TextKey[]) {
    const names = (s: string) => (s.match(/\{\w+\}/g) ?? []).sort();
    assert.deepEqual(names(ar[key]), names(en[key]), key);
    assert.ok(ar[key].trim().length > 0, key);
  }
});

test('placeholders are filled', () => {
  assert.equal(translate('en', 'book.free', { n: 7 }), '7 free');
  assert.equal(translate('ar', 'slots.booked', { b: 3, c: 20 }), '3 من 20');
});

test('API enum values have text, unknown values fall back to the raw value', () => {
  for (const status of ['Draft', 'Confirmed', 'EnRoute', 'Arrived', 'GateIn', 'Completed', 'Cancelled', 'NoShow', 'RejectedAtGate']) {
    assert.notEqual(translateDynamic('ar', `status.${status}`, status), status);
  }
  for (const light of ['Green', 'Amber', 'Red', 'Grey']) assert.ok(`light.${light}` in en);
  for (const reason of ['NoAppointment', 'Early', 'Late', 'NotCleared', 'Blocked', 'AlreadyIn', 'Cancelled']) assert.ok(`reason.${reason}` in en);
  assert.equal(translateDynamic('en', 'status.Teleported', 'Teleported'), 'Teleported');
});
