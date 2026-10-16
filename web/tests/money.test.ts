import assert from 'node:assert/strict';
import { test } from 'node:test';
import { fromMinor, money, toMinor } from '../src/lib/money.ts';

test('amounts are typed in major units and kept in the smallest unit', () => {
  assert.equal(toMinor('25', 2), 2500);
  assert.equal(toMinor('25.5', 2), 2550);
  assert.equal(toMinor('12,345', 3), 12345);
  assert.equal(toMinor('', 2), 0);
  assert.equal(toMinor('2.555', 2), null);
  assert.equal(toMinor('abc', 2), null);
  assert.equal(fromMinor(12500, 3), '12.500');
});

test('money shows the currency code and Latin digits in both languages', () => {
  assert.match(money(2500, 'SAR', 2, 'en'), /SAR\s?25\.00/);
  assert.match(money(2500, 'SAR', 2, 'ar'), /25\.00/);
  assert.match(money(2500, 'SAR', 2, 'ar'), /SAR/);
});
