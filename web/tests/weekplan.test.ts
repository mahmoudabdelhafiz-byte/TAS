import assert from 'node:assert/strict';
import test from 'node:test';
import { cellKey, compress, expand, rectangle, weekTotals, type PlanWindow } from '../src/lib/weekplan.ts';

const w = (day: PlanWindow['day'], start: string, end: string, total: number, quotas: Record<string, number> = {}): PlanWindow =>
  ({ day, start, end, totalCapacity: total, overbookPct: 0, quotas });

test('windows expand to one cell per slot and compress back unchanged', () => {
  const windows = [w('Sunday', '06:00', '12:00', 40, { IMP_PICKUP: 15 }), w('Sunday', '12:00', '24:00', 30), w('Friday', '08:00', '10:00', 10)];
  const grid = expand(windows, 60);
  assert.equal(grid.size, 6 + 12 + 2);
  assert.deepEqual(grid.get(cellKey('Sunday', 360)), { total: 40, overbookPct: 0, quotas: { IMP_PICKUP: 15 } });
  assert.equal(grid.get(cellKey('Sunday', 300)), undefined);
  assert.deepEqual(compress(grid, 60), windows);
});

test('an edited hour splits a window; equal neighbours merge again', () => {
  const grid = expand([w('Monday', '06:00', '10:00', 20)], 60);
  grid.set(cellKey('Monday', 480), { total: 5, overbookPct: 0, quotas: {} });
  assert.deepEqual(compress(grid, 60).map(x => `${x.start}-${x.end}:${x.totalCapacity}`), ['06:00-08:00:20', '08:00-09:00:5', '09:00-10:00:20']);
  grid.set(cellKey('Monday', 480), { total: 20, overbookPct: 0, quotas: {} });
  assert.equal(compress(grid, 60).length, 1);
  grid.delete(cellKey('Monday', 480));
  assert.equal(compress(grid, 60).length, 2, 'a closed hour splits the window');
});

test('half-hour slots and selection rectangles', () => {
  const grid = expand([w('Tuesday', '07:30', '09:00', 8)], 30);
  assert.equal(grid.size, 3);
  assert.deepEqual(rectangle({ day: 2, minute: 480 }, { day: 1, minute: 420 }, 30), [
    'Monday|420', 'Monday|450', 'Monday|480', 'Tuesday|420', 'Tuesday|450', 'Tuesday|480',
  ]);
  assert.deepEqual(weekTotals(grid), { slots: 3, trucks: 24 });
});
