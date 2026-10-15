// Terminal map geometry for the map editor: Web Mercator view, road network routing (same rules as the server's
// RoadGraph: roads join where they share a point, one-way roads only in their drawn direction), map checks, GeoJSON import.

export type LonLat = [number, number];

export const ZONE_TYPES = ['YARD_ZONE', 'GATE', 'EXIT', 'PRE_GATE', 'PARKING', 'CFS', 'APPROACH'] as const;
export type ZoneType = (typeof ZONE_TYPES)[number];

export interface AreaShape {
  name: string;
  zoneType: string;
  yardBlock: string | null;
  triggersCheckIn: boolean;
  /** Open ring: the first point is not repeated at the end. */
  ring: LonLat[];
}

export interface RoadShape {
  name: string;
  oneWay: boolean;
  points: LonLat[];
}

// ------------------------------------------------------------------ projection

/** Web Mercator world coordinates at zoom 0 (0..256 on both axes), the same grid map tiles use. */
export function toWorld([lon, lat]: LonLat): [number, number] {
  const clamped = Math.max(-85.05112878, Math.min(85.05112878, lat));
  const s = Math.sin((clamped * Math.PI) / 180);
  return [((lon + 180) / 360) * 256, (0.5 - Math.log((1 + s) / (1 - s)) / (4 * Math.PI)) * 256];
}

export function fromWorld([x, y]: [number, number]): LonLat {
  const lon = (x / 256) * 360 - 180;
  const n = Math.PI - (2 * Math.PI * y) / 256;
  const lat = (180 / Math.PI) * Math.atan(0.5 * (Math.exp(n) - Math.exp(-n)));
  return [lon, lat];
}

/** What part of the world the map shows: centre in world coordinates and screen pixels per world unit. */
export interface View {
  cx: number;
  cy: number;
  scale: number;
  width: number;
  height: number;
}

export function toScreen(view: View, p: LonLat): [number, number] {
  const [x, y] = toWorld(p);
  return [(x - view.cx) * view.scale + view.width / 2, (y - view.cy) * view.scale + view.height / 2];
}

export function fromScreen(view: View, sx: number, sy: number): LonLat {
  return fromWorld([(sx - view.width / 2) / view.scale + view.cx, (sy - view.height / 2) / view.scale + view.cy]);
}

/** A view that shows every point with a margin; a default spot when there is nothing yet. */
export function fitView(points: LonLat[], width: number, height: number, fallback: LonLat = [39.17, 21.47]): View {
  if (points.length === 0) {
    const [cx, cy] = toWorld(fallback);
    return { cx, cy, scale: 2 ** 16, width, height };
  }
  const world = points.map(toWorld);
  const xs = world.map(p => p[0]);
  const ys = world.map(p => p[1]);
  const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys);
  const spanX = Math.max(maxX - minX, 1e-6);
  const spanY = Math.max(maxY - minY, 1e-6);
  const margin = 48;
  const scale = Math.min((width - 2 * margin) / spanX, (height - 2 * margin) / spanY, 2 ** 22);
  return { cx: (minX + maxX) / 2, cy: (minY + maxY) / 2, scale: Math.max(scale, 1), width, height };
}

/** Zoom by `factor` keeping the point under the cursor still. */
export function zoomAt(view: View, sx: number, sy: number, factor: number): View {
  const scale = Math.min(Math.max(view.scale * factor, 2 ** 8), 2 ** 23);
  const wx = (sx - view.width / 2) / view.scale + view.cx;
  const wy = (sy - view.height / 2) / view.scale + view.cy;
  return { ...view, scale, cx: wx - (sx - view.width / 2) / scale, cy: wy - (sy - view.height / 2) / scale };
}

/** Screen metres per pixel, for the scale bar. */
export function metersPerPixel(view: View): number {
  const [, lat] = fromWorld([view.cx, view.cy]);
  return (40075016.686 * Math.cos((lat * Math.PI) / 180)) / (256 * view.scale);
}

// ------------------------------------------------------------------ distances and shapes

export function metersBetween(a: LonLat, b: LonLat): number {
  const r = 6371008.8;
  const toRad = Math.PI / 180;
  const dLat = (b[1] - a[1]) * toRad;
  const dLon = (b[0] - a[0]) * toRad;
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(a[1] * toRad) * Math.cos(b[1] * toRad) * Math.sin(dLon / 2) ** 2;
  return 2 * r * Math.asin(Math.min(1, Math.sqrt(h)));
}

export function pathMeters(path: LonLat[]): number {
  let total = 0;
  for (let i = 1; i < path.length; i++) total += metersBetween(path[i - 1], path[i]);
  return total;
}

/** Plain average of the ring's corners — what the server uses as the block's routing point. */
export function centroid(ring: LonLat[]): LonLat {
  const pts = ring.length > 1 && same(ring[0], ring[ring.length - 1]) ? ring.slice(0, -1) : ring;
  if (pts.length === 0) return [0, 0];
  return [pts.reduce((s, p) => s + p[0], 0) / pts.length, pts.reduce((s, p) => s + p[1], 0) / pts.length];
}

export function pointInRing(p: LonLat, ring: LonLat[]): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, yi] = ring[i];
    const [xj, yj] = ring[j];
    if (yi > p[1] !== yj > p[1] && p[0] < ((xj - xi) * (p[1] - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

/** Closest point on segment ab to p, in screen space, with how far along (0..1). */
export function closestOnSegment(p: [number, number], a: [number, number], b: [number, number]): { point: [number, number]; t: number; distance: number } {
  const dx = b[0] - a[0];
  const dy = b[1] - a[1];
  const len2 = dx * dx + dy * dy;
  const t = len2 === 0 ? 0 : Math.max(0, Math.min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / len2));
  const point: [number, number] = [a[0] + t * dx, a[1] + t * dy];
  return { point, t, distance: Math.hypot(p[0] - point[0], p[1] - point[1]) };
}

export const same = (a: LonLat, b: LonLat) => key(a) === key(b);

/** Points that agree to 6 decimals (about 10 cm) are one junction, as on the server. */
export const key = (p: LonLat) => `${Math.round(p[0] * 1e6)},${Math.round(p[1] * 1e6)}`;

export const round6 = (p: LonLat): LonLat => [Math.round(p[0] * 1e6) / 1e6, Math.round(p[1] * 1e6) / 1e6];

// ------------------------------------------------------------------ snapping

export type Snap =
  | { kind: 'vertex'; point: LonLat }
  | { kind: 'segment'; point: LonLat; road: number; index: number };

/** Where a click near the road network lands: on an existing point first, then on a road (which gets a new point there). */
export function snapToRoads(view: View, roads: RoadShape[], sx: number, sy: number, tolerance = 10): Snap | null {
  let best: Snap | null = null;
  let bestD = tolerance;
  for (const road of roads) {
    for (const p of road.points) {
      const [x, y] = toScreen(view, p);
      const d = Math.hypot(x - sx, y - sy);
      if (d <= bestD) { bestD = d; best = { kind: 'vertex', point: p }; }
    }
  }
  if (best) return best;
  roads.forEach((road, r) => {
    for (let i = 1; i < road.points.length; i++) {
      const hit = closestOnSegment([sx, sy], toScreen(view, road.points[i - 1]), toScreen(view, road.points[i]));
      if (hit.distance <= bestD) {
        bestD = hit.distance;
        best = { kind: 'segment', point: round6(fromScreen(view, hit.point[0], hit.point[1])), road: r, index: i };
      }
    }
  });
  return best;
}

/** Puts the snap point into the road it landed on, so the new road joins it there. */
export function applySnap(roads: RoadShape[], snap: Snap): RoadShape[] {
  if (snap.kind !== 'segment') return roads;
  return roads.map((road, r) => r !== snap.road ? road : { ...road, points: [...road.points.slice(0, snap.index), snap.point, ...road.points.slice(snap.index)] });
}

/** Moves a road point and every road point at the same spot, so junctions stay joined. */
export function moveJunction(roads: RoadShape[], from: LonLat, to: LonLat): RoadShape[] {
  const k = key(from);
  return roads.map(road => road.points.some(p => key(p) === k)
    ? { ...road, points: road.points.map(p => (key(p) === k ? to : p)) }
    : road);
}

// ------------------------------------------------------------------ routing

export class RoadGraph {
  readonly nodes: LonLat[] = [];
  private readonly index = new Map<string, number>();
  private readonly edges: { to: number; meters: number }[][] = [];

  constructor(roads: RoadShape[]) {
    for (const road of roads) {
      for (let i = 1; i < road.points.length; i++) {
        const a = this.node(road.points[i - 1]);
        const b = this.node(road.points[i]);
        if (a === b) continue;
        const meters = metersBetween(this.nodes[a], this.nodes[b]);
        this.edges[a].push({ to: b, meters });
        if (!road.oneWay) this.edges[b].push({ to: a, meters });
      }
    }
  }

  private node(p: LonLat): number {
    const k = key(p);
    let i = this.index.get(k);
    if (i === undefined) {
      i = this.nodes.length;
      this.index.set(k, i);
      this.nodes.push(p);
      this.edges.push([]);
    }
    return i;
  }

  nearest(p: LonLat): number | null {
    let best: number | null = null;
    let bestD = Infinity;
    this.nodes.forEach((n, i) => {
      const d = metersBetween(n, p);
      if (d < bestD) { bestD = d; best = i; }
    });
    return best;
  }

  /** Shortest drive between the road points nearest to `from` and `to`; null when the roads don't connect them. */
  route(from: LonLat, to: LonLat): { path: LonLat[]; meters: number } | null {
    const s = this.nearest(from);
    const t = this.nearest(to);
    if (s === null || t === null) return null;
    const dist = new Array<number>(this.nodes.length).fill(Infinity);
    const prev = new Array<number>(this.nodes.length).fill(-1);
    const done = new Array<boolean>(this.nodes.length).fill(false);
    dist[s] = 0;
    // The yard has hundreds of road points at most, so a simple scan is fast enough.
    for (;;) {
      let u = -1;
      for (let i = 0; i < dist.length; i++) if (!done[i] && dist[i] < Infinity && (u < 0 || dist[i] < dist[u])) u = i;
      if (u < 0 || u === t) break;
      done[u] = true;
      for (const e of this.edges[u]) {
        if (dist[u] + e.meters < dist[e.to]) { dist[e.to] = dist[u] + e.meters; prev[e.to] = u; }
      }
    }
    if (dist[t] === Infinity) return null;
    const path: LonLat[] = [];
    for (let v = t; v >= 0; v = prev[v]) path.unshift(this.nodes[v]);
    return { path, meters: dist[t] };
  }

  /** Groups of road points joined to each other (ignoring direction). */
  pieces(): number {
    const parent = this.nodes.map((_, i) => i);
    const find = (i: number): number => (parent[i] === i ? i : (parent[i] = find(parent[i])));
    this.edges.forEach((list, a) => list.forEach(e => { parent[find(a)] = find(e.to); }));
    return new Set(this.nodes.map((_, i) => find(i))).size;
  }
}

// ------------------------------------------------------------------ map checks

export type Problem =
  | { kind: 'noRoads' }
  | { kind: 'noGate' }
  | { kind: 'noExit' }
  | { kind: 'pieces'; count: number }
  | { kind: 'noBlock'; area: string }
  | { kind: 'unreachable'; block: string }
  | { kind: 'noWayOut'; block: string }
  | { kind: 'duplicateBlock'; block: string };

/** What would stop the driver app from routing every truck: the gate, the exit, every block reachable and left again. */
export function checkMap(areas: AreaShape[], roads: RoadShape[]): Problem[] {
  const problems: Problem[] = [];
  const gate = areas.find(a => a.zoneType === 'GATE');
  const exit = areas.find(a => a.zoneType === 'EXIT');
  if (roads.length === 0) problems.push({ kind: 'noRoads' });
  if (!gate) problems.push({ kind: 'noGate' });
  if (!exit) problems.push({ kind: 'noExit' });

  const seen = new Set<string>();
  for (const a of areas.filter(x => x.zoneType === 'YARD_ZONE')) {
    const block = a.yardBlock?.trim().toUpperCase();
    if (!block) { problems.push({ kind: 'noBlock', area: a.name }); continue; }
    if (seen.has(block)) problems.push({ kind: 'duplicateBlock', block });
    seen.add(block);
  }
  if (roads.length === 0) return problems;

  const graph = new RoadGraph(roads);
  const pieces = graph.pieces();
  if (pieces > 1) problems.push({ kind: 'pieces', count: pieces });

  for (const a of areas.filter(x => x.zoneType === 'YARD_ZONE' && x.yardBlock?.trim())) {
    const block = a.yardBlock!.trim().toUpperCase();
    const at = centroid(a.ring);
    if (gate && !graph.route(centroid(gate.ring), at)) problems.push({ kind: 'unreachable', block });
    if (exit && !graph.route(at, centroid(exit.ring))) problems.push({ kind: 'noWayOut', block });
  }
  return problems;
}

// ------------------------------------------------------------------ GeoJSON in and out

export function roadsToGeoJson(roads: RoadShape[]) {
  return {
    type: 'FeatureCollection',
    features: roads.map(r => ({
      type: 'Feature',
      properties: { name: r.name || null, oneWay: r.oneWay },
      geometry: { type: 'LineString', coordinates: r.points.map(round6) },
    })),
  };
}

interface Feature {
  type?: string;
  properties?: Record<string, unknown> | null;
  geometry?: { type?: string; coordinates?: unknown } | null;
}

function coords(value: unknown): LonLat[] {
  if (!Array.isArray(value)) return [];
  return value
    .filter((c): c is number[] => Array.isArray(c) && c.length >= 2 && typeof c[0] === 'number' && typeof c[1] === 'number')
    .filter(c => Math.abs(c[0]) <= 180 && Math.abs(c[1]) <= 90)
    .map(c => [c[0], c[1]] as LonLat);
}

const text = (v: unknown) => (typeof v === 'string' && v.trim() ? v.trim() : null);

function zoneOf(props: Record<string, unknown>): string {
  const raw = (text(props.zoneType) ?? text(props.kind) ?? text(props.type) ?? '').toUpperCase().replace(/[\s-]+/g, '_');
  if ((ZONE_TYPES as readonly string[]).includes(raw)) return raw;
  if (raw === 'BLOCK' || raw === 'YARD') return 'YARD_ZONE';
  if (raw === 'PREGATE') return 'PRE_GATE';
  return 'YARD_ZONE';
}

/** Reads a GeoJSON file from a CAD / GIS export: polygons become areas, lines become roads. */
export function importGeoJson(json: unknown): { areas: AreaShape[]; roads: RoadShape[]; skipped: number } {
  const areas: AreaShape[] = [];
  const roads: RoadShape[] = [];
  let skipped = 0;
  const root = json as { type?: string; features?: Feature[] } & Feature;
  const features: Feature[] = root?.type === 'FeatureCollection' && Array.isArray(root.features) ? root.features
    : root?.type === 'Feature' ? [root]
    : [];

  for (const f of features) {
    const props = f.properties ?? {};
    const g = f.geometry;
    const lines = g?.type === 'LineString' ? [coords(g.coordinates)]
      : g?.type === 'MultiLineString' && Array.isArray(g.coordinates) ? g.coordinates.map(coords)
      : null;
    const rings = g?.type === 'Polygon' && Array.isArray(g.coordinates) ? [coords(g.coordinates[0])]
      : g?.type === 'MultiPolygon' && Array.isArray(g.coordinates) ? g.coordinates.map(p => coords(Array.isArray(p) ? p[0] : null))
      : null;

    if (lines) {
      for (const points of lines) {
        if (points.length < 2) { skipped++; continue; }
        roads.push({ name: text(props.name) ?? '', oneWay: props.oneWay === true || props.oneway === 'yes' || props.oneway === true, points: points.map(round6) });
      }
    } else if (rings) {
      for (const ring of rings) {
        const open = ring.length > 1 && same(ring[0], ring[ring.length - 1]) ? ring.slice(0, -1) : ring;
        if (open.length < 3) { skipped++; continue; }
        const zoneType = zoneOf(props);
        const yardBlock = text(props.yardBlock) ?? text(props.block) ?? null;
        areas.push({
          name: text(props.name) ?? (yardBlock ? `Block ${yardBlock}` : zoneType),
          zoneType,
          yardBlock: zoneType === 'YARD_ZONE' || zoneType === 'CFS' ? yardBlock : null,
          triggersCheckIn: props.triggersCheckIn === true,
          ring: open,
        });
      }
    } else {
      skipped++;
    }
  }
  return { areas, roads, skipped };
}

/** The server's area GeoJSON (a Polygon) as an open ring. */
export function ringOf(geoJson: string): LonLat[] {
  try {
    const g = JSON.parse(geoJson) as { type?: string; coordinates?: unknown[] };
    if (g.type !== 'Polygon' || !Array.isArray(g.coordinates)) return [];
    const ring = coords(g.coordinates[0]);
    return ring.length > 1 && same(ring[0], ring[ring.length - 1]) ? ring.slice(0, -1) : ring;
  } catch {
    return [];
  }
}

export function roadsFromGeoJson(json: unknown): RoadShape[] {
  return importGeoJson(json).roads;
}
