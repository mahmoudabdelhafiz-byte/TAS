import assert from 'node:assert/strict';
import { test } from 'node:test';
import { applyBrand, contrastWithWhite, defaultBrand, shades } from '../src/lib/brand.ts';

test('the main colour gives a darker shade and a pale tint', () => {
  assert.deepEqual(shades('#0E7C86'), { main: '#0e7c86', dark: '#0b6169', tint: '#e4f1f2' });
  assert.equal(shades('not a colour').main, defaultBrand.color);
});

test('contrast with white matches the server\'s check', () => {
  assert.ok(Math.abs(contrastWithWhite('#000000') - 21) < 0.01);
  assert.ok(contrastWithWhite('#0e7c86') >= 4.5);
  assert.ok(contrastWithWhite('#ff9900') < 4.5);
});

test('the brand\'s colours replace the Yardsyncs teal', () => {
  const set: Record<string, string> = {};
  applyBrand({ name: 'Harbor One', color: '#7a1f3d', logoUrl: null, operatorId: 1 }, { style: { setProperty: (k, v) => { set[k] = v; } } });
  assert.equal(set['--teal'], '#7a1f3d');
  assert.equal(set['--tone-1'], '#7a1f3d');
  assert.ok(set['--teal-dark'] < '#7a1f3d' && set['--teal-tint'] > '#e00000');
});
