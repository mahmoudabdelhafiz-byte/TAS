import assert from 'node:assert/strict';
import test from 'node:test';
import { readLookup } from '../src/lib/gate.ts';
import { buildHash, parseHash, screensFor } from '../src/lib/routes.ts';

test('hash routes round-trip', () => {
  const hash = buildHash('appointments', { date: '2026-10-20', no: 'DEMO-261020-7KQ4M', empty: undefined });
  assert.equal(hash, '#/appointments?date=2026-10-20&no=DEMO-261020-7KQ4M');
  assert.deepEqual(parseHash(hash), { screen: 'appointments', params: { date: '2026-10-20', no: 'DEMO-261020-7KQ4M' } });
  assert.deepEqual(parseHash(''), { screen: null, params: {} });
  assert.equal(parseHash('#/nowhere').screen, null);
});

test('each role gets its own menu', () => {
  assert.deepEqual(screensFor('CompanyUser'), ['book', 'bulk', 'appointments', 'fleet']);
  assert.ok(!screensFor('CompanyAdmin').includes('kpi'));
  assert.deepEqual(screensFor('GateClerk'), ['gate', 'control', 'appointments']);
  assert.ok(screensFor('Planner').includes('control') && !screensFor('CompanyUser').includes('control'));
  assert.equal(screensFor('Planner')[0], 'slots');
  assert.ok(!screensFor('CompanyAdmin').includes('gate'));
  assert.ok(screensFor('TerminalAdmin').includes('map') && screensFor('Planner').includes('map'));
  assert.ok(!screensFor('CompanyAdmin').includes('map') && !screensFor('GateClerk').includes('map'));
  assert.equal(parseHash('#/map').screen, 'map');
});

test('gate lookup tells appointment numbers from plates', () => {
  assert.deepEqual(readLookup(' demo-261020-7kq4m '), { appointmentNo: 'DEMO-261020-7KQ4M' });
  assert.deepEqual(readLookup('1234 abj'), { plateRead: '1234 ABJ' });
  assert.deepEqual(readLookup('  '), {});
});
