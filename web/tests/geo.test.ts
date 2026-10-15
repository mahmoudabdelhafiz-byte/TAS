import assert from 'node:assert/strict';
import test from 'node:test';
import {
  applySnap, centroid, checkMap, fitView, fromScreen, importGeoJson, metersBetween, moveJunction, pointInRing,
  RoadGraph, ringOf, roadsToGeoJson, snapToRoads, toScreen, zoomAt, type AreaShape, type LonLat, type RoadShape,
} from '../src/lib/geo.ts';

// Same square as the server's RoadGraphTests: the west road bends out, the east road is one-way southbound.
const sw: LonLat = [0, 0], nw: LonLat = [0, 0.002], ne: LonLat = [0.002, 0.002], se: LonLat = [0.002, 0];
const square: RoadShape[] = [
  { name: 'south', oneWay: false, points: [sw, se] },
  { name: 'north', oneWay: false, points: [nw, ne] },
  { name: 'west', oneWay: false, points: [sw, [-0.0005, 0.001], nw] },
  { name: 'east', oneWay: true, points: [ne, se] },
];

test('routes follow one-way roads, like the server', () => {
  const graph = new RoadGraph(square);
  const down = graph.route(ne, se)!;
  assert.equal(down.path.length, 2);
  assert.ok(down.meters > 200 && down.meters < 245, `${down.meters}`);
  const up = graph.route(se, ne)!; // against the one-way: round by the west
  assert.equal(up.path.length, 5);
  assert.ok(up.meters > 650 && up.meters < 730, `${up.meters}`);
  assert.equal(graph.pieces(), 1);
});

test('disconnected roads are found and not routed', () => {
  const island: RoadShape = { name: '', oneWay: false, points: [[0.01, 0.01], [0.011, 0.01]] };
  const graph = new RoadGraph([...square, island]);
  assert.equal(graph.pieces(), 2);
  assert.equal(graph.route(sw, [0.0105, 0.0101]), null);
  assert.equal(new RoadGraph([]).route(sw, ne), null);
});

const box = (x: number, y: number, s = 0.0004): LonLat[] => [[x, y], [x + s, y], [x + s, y + s], [x, y + s]];
const area = (zoneType: string, ring: LonLat[], yardBlock: string | null = null, name = zoneType): AreaShape =>
  ({ name, zoneType, yardBlock, triggersCheckIn: false, ring });

test('map check: gate, exit, blocks reachable and left again', () => {
  const areas = [area('GATE', box(-0.0002, 0.0018)), area('EXIT', box(0.0018, -0.0002)), area('YARD_ZONE', box(0.0008, 0.0008), 'a1')];
  assert.deepEqual(checkMap(areas, square), []);

  // Without the south road, nothing leaves the south-east corner (the east road is one-way into it):
  // a gate there reaches no block.
  const noSouth = square.filter(r => r.name !== 'south');
  const flipped = [area('GATE', box(0.0018, -0.0002)), area('EXIT', box(-0.0002, 0.0018)), area('YARD_ZONE', box(0.0012, 0.0014), 'A1')];
  assert.deepEqual(checkMap(flipped, noSouth), [{ kind: 'unreachable', block: 'A1' }]);

  const missing = checkMap([area('YARD_ZONE', box(0, 0)), area('YARD_ZONE', box(0, 0), 'B1'), area('YARD_ZONE', box(0, 0), 'b1')], []);
  assert.deepEqual(missing.map(p => p.kind).sort(), ['duplicateBlock', 'noBlock', 'noExit', 'noGate', 'noRoads']);
});

test('snapping joins a new road onto an existing one', () => {
  const view = fitView([sw, ne], 400, 400);
  const [x, y] = toScreen(view, [0.001, 0]); // middle of the south road
  const snap = snapToRoads(view, square, x + 3, y - 4)!;
  assert.equal(snap.kind, 'segment');
  const roads = applySnap(square, snap);
  assert.equal(roads[0].points.length, 3);
  const [vx, vy] = toScreen(view, ne);
  assert.deepEqual(snapToRoads(view, square, vx + 2, vy + 2), { kind: 'vertex', point: ne });
  assert.equal(snapToRoads(view, square, 200, 200), null);

  // Dragging a junction moves it on every road that meets there.
  const moved = moveJunction(square, ne, [0.0021, 0.0021]);
  assert.deepEqual(moved[1].points[1], [0.0021, 0.0021]);
  assert.deepEqual(moved[3].points[0], [0.0021, 0.0021]);
  assert.equal(new RoadGraph(moved).pieces(), 1);
});

test('screen projection round-trips and zoom keeps the cursor point', () => {
  const view = fitView([[39.164, 21.4615], [39.1765, 21.471]], 800, 600);
  const p: LonLat = [39.17, 21.465];
  const [x, y] = toScreen(view, p);
  const back = fromScreen(view, x, y);
  assert.ok(metersBetween(p, back) < 0.01);
  const zoomed = zoomAt(view, x, y, 2);
  const [x2, y2] = toScreen(zoomed, p);
  assert.ok(Math.abs(x2 - x) < 1e-6 && Math.abs(y2 - y) < 1e-6);
  for (const c of [[39.164, 21.4615], [39.1765, 21.471]] as LonLat[]) {
    const [cx, cy] = toScreen(view, c);
    assert.ok(cx >= 47 && cx <= 753 && cy >= 47 && cy <= 553, `${cx},${cy}`);
  }
});

test('GeoJSON import from a CAD export', () => {
  const file = {
    type: 'FeatureCollection',
    features: [
      { type: 'Feature', properties: { name: 'Block 7', block: 'C07' }, geometry: { type: 'Polygon', coordinates: [[[1, 1], [2, 1], [2, 2], [1, 2], [1, 1]]] } },
      { type: 'Feature', properties: { kind: 'gate' }, geometry: { type: 'Polygon', coordinates: [[[3, 3], [4, 3], [4, 4], [3, 3]]] } },
      { type: 'Feature', properties: { oneway: 'yes', name: 'Ring road' }, geometry: { type: 'MultiLineString', coordinates: [[[0, 0], [1, 0]], [[1, 0], [1, 1]]] } },
      { type: 'Feature', properties: {}, geometry: { type: 'Point', coordinates: [0, 0] } },
      { type: 'Feature', properties: {}, geometry: { type: 'LineString', coordinates: [[0, 0]] } },
    ],
  };
  const { areas, roads, skipped } = importGeoJson(file);
  assert.equal(areas.length, 2);
  assert.deepEqual(areas[0], { name: 'Block 7', zoneType: 'YARD_ZONE', yardBlock: 'C07', triggersCheckIn: false, ring: [[1, 1], [2, 1], [2, 2], [1, 2]] });
  assert.equal(areas[1].zoneType, 'GATE');
  assert.equal(areas[1].yardBlock, null);
  assert.equal(roads.length, 2);
  assert.ok(roads.every(r => r.oneWay && r.name === 'Ring road'));
  assert.equal(skipped, 2);
  assert.deepEqual(importGeoJson({ nonsense: true }), { areas: [], roads: [], skipped: 0 });

  // Saved roads read back the same.
  assert.deepEqual(importGeoJson(roadsToGeoJson(square)).roads, square);
});

test('shapes', () => {
  assert.deepEqual(ringOf('{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}'), [[0, 0], [1, 0], [1, 1]]);
  assert.deepEqual(ringOf('nope'), []);
  assert.deepEqual(centroid([[0, 0], [2, 0], [2, 2], [0, 2]]), [1, 1]);
  assert.ok(pointInRing([1, 1], [[0, 0], [2, 0], [2, 2], [0, 2]]));
  assert.ok(!pointInRing([3, 1], [[0, 0], [2, 0], [2, 2], [0, 2]]));
});
