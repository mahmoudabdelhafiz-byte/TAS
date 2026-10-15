import assert from 'node:assert/strict';
import test from 'node:test';
import { allocate, groupTrucks, splitContainers, type PlanContainer, type PlanSlot } from '../src/lib/plan.ts';

const c = (no: string, sizeFt: number | null): PlanContainer => ({ containerNo: no, isoType: null, sizeFt });
const nos = (trucks: PlanContainer[][]) => trucks.map(t => t.map(x => x.containerNo).join('+'));

test('twenties pair up, forties go alone, unknown sizes pair only when asked', () => {
  const list = [c('A', 20), c('B', 40), c('C', 20), c('D', 20), c('E', null), c('F', 45)];
  assert.deepEqual(nos(groupTrucks(list, false)), ['A', 'B', 'C', 'D', 'E', 'F']);
  assert.deepEqual(nos(groupTrucks(list, true)), ['B', 'A+C', 'E', 'F', 'D']);
  assert.deepEqual(nos(groupTrucks(list, true, true)), ['B', 'A+C', 'D+E', 'F']);
});

test('trucks fill hours in time order, counting each container as one place', () => {
  const slots: PlanSlot[] = [
    { slotId: 2, startAt: '2026-10-16T08:00:00Z', endAt: '2026-10-16T09:00:00Z', available: 1 },
    { slotId: 1, startAt: '2026-10-16T07:00:00Z', endAt: '2026-10-16T08:00:00Z', available: 3 },
  ];
  const trucks = [[c('A', 20), c('B', 20)], [c('C', 20), c('D', 20)], [c('E', 40)], [c('F', 40)]];
  const { visits, unplaced } = allocate(trucks, slots);
  assert.deepEqual(visits.map(v => `${v.slot.slotId}:${v.containers.map(x => x.containerNo).join('+')}`), ['1:A+B', '1:E', '2:F']);
  assert.deepEqual(nos(unplaced), ['C+D']);
});

test('pasted text splits into distinct container numbers', () => {
  assert.deepEqual(
    splitContainers('MSCU1234566\nmscu 552341-7, TGHU8812340;MSCU1234566\nCMAU5100028 MEDU2601003\t'),
    ['MSCU1234566', 'MSCU5523417', 'TGHU8812340', 'CMAU5100028', 'MEDU2601003'],
  );
});
