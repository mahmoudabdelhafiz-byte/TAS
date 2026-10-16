import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react';
import { ApiError } from '../lib/api.ts';
import {
  applySnap, centroid, checkMap, fitView, fromScreen, importGeoJson, key, metersPerPixel, moveJunction, pathMeters,
  pointInRing, RoadGraph, ringOf, round6, roadsFromGeoJson, roadsToGeoJson, snapToRoads, toScreen, toWorld, zoomAt,
  ZONE_TYPES, closestOnSegment, type AreaShape, type LonLat, type Problem, type RoadShape, type View,
} from '../lib/geo.ts';
import type { TextKey } from '../lib/i18n.ts';
import type { GeofenceView } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { LoadError, Notice, messageOf } from '../ui/kit.tsx';

interface DraftArea extends AreaShape {
  key: string;
  id: number | null;
  dirty: boolean;
}

type Tool = 'select' | 'area' | 'road' | 'route';
type Selection = { type: 'area'; key: string } | { type: 'road'; index: number } | null;

type Drag =
  | { kind: 'pan'; x: number; y: number; view: View; moved: boolean }
  | { kind: 'vertex'; target: 'area' | 'road'; ref: string | number; index: number; at: LonLat; moved: boolean }
  | null;

/** Areas the driver app draws; the rest (approach roads, pre-gate) only matter for arrival and check-in. */
const FRAME_KINDS = ['YARD_ZONE', 'CFS', 'GATE', 'EXIT', 'PARKING', 'PRE_GATE'];

let nextKey = 1;
const newKey = () => `new-${nextKey++}`;

/** Tile server for the background picture, set per installation in index.html (empty = plain plan, e.g. on-premise without internet). */
function meta(name: string): string {
  if (typeof document === 'undefined') return '';
  return document.querySelector<HTMLMetaElement>(`meta[name="${name}"]`)?.content.trim() ?? '';
}

export function MapEditor() {
  const { api, t, tx, user, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  const canEdit = user.role === 'SysAdmin' || user.role === 'OperatorAdmin' || (user.role === 'TerminalAdmin' && user.terminalId === terminal.terminalId);

  const [areas, setAreas] = useState<DraftArea[] | null>(null);
  const [removed, setRemoved] = useState<number[]>([]);
  const [roads, setRoads] = useState<RoadShape[] | null>(null);
  const [roadsDirty, setRoadsDirty] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loadTick, setLoadTick] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const [tool, setTool] = useState<Tool>('select');
  const [selected, setSelected] = useState<Selection>(null);
  const [drawing, setDrawing] = useState<LonLat[]>([]);
  const [cursor, setCursor] = useState<LonLat | null>(null);
  const [routeEnds, setRouteEnds] = useState<LonLat[]>([]);
  const [showTiles, setShowTiles] = useState(true);

  const [size, setSize] = useState<{ width: number; height: number } | null>(null);
  const [view, setView] = useState<View | null>(null);
  const drag = useRef<Drag>(null);
  const boxRef = useRef<HTMLDivElement>(null);
  const svgRef = useRef<SVGSVGElement>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  const tiles = meta('yardsyncs-map-tiles');
  const attribution = meta('yardsyncs-map-attribution');

  // ---------------------------------------------------------------- load

  useEffect(() => {
    let live = true;
    Promise.all([api.get<GeofenceView[]>(`${base}/geofences`), api.get<unknown>(`${base}/yard-map/roads`)])
      .then(([list, roadJson]) => {
        if (!live) return;
        setAreas(list.filter(a => a.isActive).map(a => ({
          key: `id-${a.geofenceId}`, id: a.geofenceId, dirty: false, name: a.name, zoneType: a.zoneType,
          yardBlock: a.yardBlock, triggersCheckIn: a.triggersCheckIn, ring: ringOf(a.geoJson),
        })));
        setRoads(roadsFromGeoJson(roadJson));
        setRemoved([]);
        setRoadsDirty(false);
        setLoadError(null);
      })
      .catch(e => { if (live) setLoadError(messageOf(e, t('common.offline'))); });
    return () => { live = false; };
  }, [api, base, loadTick, t]);

  useEffect(() => {
    const box = boxRef.current;
    if (!box) return;
    const observer = new ResizeObserver(entries => {
      const r = entries[0].contentRect;
      setSize({ width: Math.round(r.width), height: Math.round(r.height) });
    });
    observer.observe(box);
    return () => observer.disconnect();
  }, [areas !== null]);

  const framePoints = useCallback((): LonLat[] => {
    const pts = [
      ...(areas ?? []).filter(a => FRAME_KINDS.includes(a.zoneType)).flatMap(a => a.ring),
      ...(roads ?? []).flatMap(r => r.points),
    ];
    return pts.length > 0 ? pts : (areas ?? []).flatMap(a => a.ring);
  }, [areas, roads]);

  // First fit once the data and the box size are known; afterwards keep the user's pan and zoom.
  useEffect(() => {
    if (!size || !areas || !roads) return;
    setView(v => v ? { ...v, width: size.width, height: size.height } : fitView(framePoints(), size.width, size.height));
  }, [size, areas !== null, roads !== null]);

  const dirty = removed.length > 0 || roadsDirty || (areas ?? []).some(a => a.dirty);

  useEffect(() => {
    if (!dirty) return;
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  const problems = useMemo(() => (areas && roads ? checkMap(areas, roads) : []), [areas, roads]);
  const route = useMemo(() => {
    if (!roads || routeEnds.length < 2) return null;
    return new RoadGraph(roads).route(routeEnds[0], routeEnds[1]);
  }, [roads, routeEnds]);

  // ---------------------------------------------------------------- edits

  const updateArea = (k: string, change: Partial<AreaShape>) =>
    setAreas(list => list!.map(a => (a.key === k ? { ...a, ...change, dirty: true } : a)));

  const updateRoads = (next: RoadShape[]) => { setRoads(next); setRoadsDirty(true); };

  const removeSelected = () => {
    if (!selected || !canEdit) return;
    if (selected.type === 'area') {
      const area = areas!.find(a => a.key === selected.key);
      if (area?.id) setRemoved(r => [...r, area.id!]);
      setAreas(list => list!.filter(a => a.key !== selected.key));
    } else {
      updateRoads(roads!.filter((_, i) => i !== selected.index));
    }
    setSelected(null);
  };

  const finishDrawing = () => {
    if (tool === 'area' && drawing.length >= 3) {
      const k = newKey();
      const n = areas!.filter(a => a.zoneType === 'YARD_ZONE').length + 1;
      setAreas(list => [...list!, { key: k, id: null, dirty: true, name: t('map.newBlock', { n }), zoneType: 'YARD_ZONE', yardBlock: '', triggersCheckIn: false, ring: drawing.map(round6) }]);
      setSelected({ type: 'area', key: k });
      setTool('select');
    } else if (tool === 'road' && drawing.length >= 2) {
      updateRoads([...roads!, { name: '', oneWay: false, points: drawing.map(round6) }]);
      setSelected({ type: 'road', index: roads!.length });
      setTool('select');
    }
    setDrawing([]);
  };

  const chooseTool = (next: Tool) => {
    setTool(next);
    setDrawing([]);
    setRouteEnds([]);
    if (next !== 'select') setSelected(null);
  };

  // Keyboard: Enter finishes a drawing, Escape cancels, Backspace takes back the last point, Delete removes the selection.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && /^(INPUT|SELECT|TEXTAREA)$/.test(target.tagName)) return;
      if (e.key === 'Enter' && drawing.length > 0) { e.preventDefault(); finishDrawing(); }
      else if (e.key === 'Escape') { if (drawing.length > 0) setDrawing([]); else if (tool !== 'select') chooseTool('select'); else setSelected(null); }
      else if (e.key === 'Backspace' && drawing.length > 0) { e.preventDefault(); setDrawing(d => d.slice(0, -1)); }
      else if ((e.key === 'Delete' || e.key === 'Backspace') && selected && canEdit) { e.preventDefault(); removeSelected(); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  // ---------------------------------------------------------------- pointer

  const local = (e: { clientX: number; clientY: number }): [number, number] => {
    const r = svgRef.current!.getBoundingClientRect();
    return [e.clientX - r.left, e.clientY - r.top];
  };

  const hitTest = (sx: number, sy: number): Selection => {
    if (!view) return null;
    for (let i = roads!.length - 1; i >= 0; i--) {
      const pts = roads![i].points.map(p => toScreen(view, p));
      for (let j = 1; j < pts.length; j++) if (closestOnSegment([sx, sy], pts[j - 1], pts[j]).distance <= 7) return { type: 'road', index: i };
    }
    const p = fromScreen(view, sx, sy);
    const hits = areas!.filter(a => a.ring.length >= 3 && pointInRing(p, a.ring));
    if (hits.length === 0) return null;
    hits.sort((a, b) => ringSize(a.ring) - ringSize(b.ring)); // smallest on top
    return { type: 'area', key: hits[0].key };
  };

  const onClick = (sx: number, sy: number) => {
    if (!view) return;
    const p = fromScreen(view, sx, sy);
    if (tool === 'select') { setSelected(hitTest(sx, sy)); return; }
    if (tool === 'route') {
      setRouteEnds(ends => (ends.length >= 2 ? [p] : [...ends, p]));
      return;
    }
    // Drawing: a click on the last point (or a double-click) finishes; for areas so does a click on the first point.
    if (drawing.length > 0) {
      const [lx, ly] = toScreen(view, drawing[drawing.length - 1]);
      const [fx, fy] = toScreen(view, drawing[0]);
      if (Math.hypot(lx - sx, ly - sy) < 6 || (tool === 'area' && drawing.length >= 3 && Math.hypot(fx - sx, fy - sy) < 10)) {
        finishDrawing();
        return;
      }
    }
    if (tool === 'road') {
      const snap = snapToRoads(view, roads!, sx, sy);
      if (snap) {
        if (snap.kind === 'segment') updateRoads(applySnap(roads!, snap));
        setDrawing(d => [...d, snap.point]);
        return;
      }
    }
    setDrawing(d => [...d, round6(p)]);
  };

  const onPointerDown = (e: ReactPointerEvent<SVGSVGElement>) => {
    if (!view || e.button !== 0) return;
    const [sx, sy] = local(e);
    const handle = (e.target as Element).closest<SVGElement>('[data-handle]');
    if (handle && canEdit && selected) {
      const index = Number(handle.dataset.index);
      const mid = handle.dataset.handle === 'mid';
      if (selected.type === 'area') {
        const area = areas!.find(a => a.key === selected.key)!;
        let at = area.ring[index];
        if (mid) {
          at = midpoint(area.ring[index], area.ring[(index + 1) % area.ring.length]);
          updateArea(area.key, { ring: [...area.ring.slice(0, index + 1), at, ...area.ring.slice(index + 1)] });
        }
        drag.current = { kind: 'vertex', target: 'area', ref: area.key, index: mid ? index + 1 : index, at, moved: false };
      } else {
        const road = roads![selected.index];
        let at = road.points[index];
        if (mid) {
          at = midpoint(road.points[index], road.points[index + 1]);
          updateRoads(roads!.map((r, i) => (i === selected.index ? { ...r, points: [...r.points.slice(0, index + 1), at, ...r.points.slice(index + 1)] } : r)));
        }
        drag.current = { kind: 'vertex', target: 'road', ref: selected.index, index: mid ? index + 1 : index, at, moved: false };
      }
    } else {
      drag.current = { kind: 'pan', x: sx, y: sy, view, moved: false };
    }
    svgRef.current?.setPointerCapture(e.pointerId);
  };

  const onPointerMove = (e: ReactPointerEvent<SVGSVGElement>) => {
    if (!view) return;
    const [sx, sy] = local(e);
    const d = drag.current;
    if (d?.kind === 'pan') {
      if (!d.moved && Math.hypot(sx - d.x, sy - d.y) < 4) return;
      d.moved = true;
      setView({ ...d.view, cx: d.view.cx - (sx - d.x) / d.view.scale, cy: d.view.cy - (sy - d.y) / d.view.scale });
    } else if (d?.kind === 'vertex') {
      d.moved = true;
      const p = round6(fromScreen(view, sx, sy));
      if (d.target === 'area') {
        const area = areas!.find(a => a.key === d.ref)!;
        updateArea(area.key, { ring: area.ring.map((q, i) => (i === d.index ? p : q)) });
      } else {
        // Junction points move on every road that meets there, so roads stay joined.
        updateRoads(moveJunction(roads!, d.at, p));
      }
      d.at = p;
    } else if (tool === 'area' || tool === 'road') {
      setCursor(fromScreen(view, sx, sy));
    }
  };

  const onPointerUp = (e: ReactPointerEvent<SVGSVGElement>) => {
    const d = drag.current;
    drag.current = null;
    if (!view) return;
    const [sx, sy] = local(e);
    if (d?.kind === 'pan' && !d.moved) onClick(sx, sy);
    if (d?.kind === 'vertex' && d.target === 'road' && d.moved) {
      // Dropped on another road's point: join them there.
      const k = key(d.at);
      const target = roads!.flatMap(r => r.points).find(p => key(p) !== k && Math.hypot(toScreen(view, p)[0] - sx, toScreen(view, p)[1] - sy) < 10);
      if (target) updateRoads(moveJunction(roads!, d.at, target));
    }
  };

  const onDoubleClick = (e: ReactMouseEvent<SVGSVGElement>) => {
    const handle = (e.target as Element).closest<SVGElement>('[data-handle="vertex"]');
    if (!handle || !selected || !canEdit) return;
    const index = Number(handle.dataset.index);
    // Double-click a corner to remove it.
    if (selected.type === 'area') {
      const area = areas!.find(a => a.key === selected.key)!;
      if (area.ring.length > 3) updateArea(area.key, { ring: area.ring.filter((_, i) => i !== index) });
    } else {
      const road = roads![selected.index];
      if (road.points.length > 2) updateRoads(roads!.map((r, i) => (i === selected.index ? { ...r, points: r.points.filter((_, j) => j !== index) } : r)));
    }
  };

  useEffect(() => {
    const svg = svgRef.current;
    if (!svg) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const r = svg.getBoundingClientRect();
      setView(v => v && zoomAt(v, e.clientX - r.left, e.clientY - r.top, Math.exp(-e.deltaY * 0.0015)));
    };
    svg.addEventListener('wheel', onWheel, { passive: false });
    return () => svg.removeEventListener('wheel', onWheel);
  }, [view !== null]);

  // ---------------------------------------------------------------- save

  /** Saves each change on its own; what fails stays as an unsaved change with the server's reason, the rest is kept. */
  async function save() {
    setSaving(true);
    setError(null);
    setDone(null);
    const failures: string[] = [];
    const ringOut = (ring: LonLat[]) => [...ring, ring[0]].map(round6);
    const stillRemoved: number[] = [];
    const savedKeys = new Map<string, number>();
    let roadsSaved = !roadsDirty;
    try {
      for (const id of removed) {
        try { await api.post(`${base}/geofences/${id}/deactivate`); }
        catch (e) { if (!(e instanceof ApiError && e.status === 404)) { stillRemoved.push(id); failures.push(messageOf(e, t('common.offline'))); } }
      }
      for (const a of areas!.filter(x => x.dirty)) {
        const body = { name: a.name, zoneType: a.zoneType, ring: ringOut(a.ring), yardBlock: a.yardBlock || null, triggersCheckIn: a.triggersCheckIn };
        try {
          if (a.id === null) savedKeys.set(a.key, (await api.post<{ geofenceId: number }>(`${base}/geofences`, body)).geofenceId);
          else { await api.put(`${base}/geofences/${a.id}`, body); savedKeys.set(a.key, a.id); }
        } catch (e) {
          failures.push(`${a.name}: ${messageOf(e, t('common.offline'))}`);
        }
      }
      if (roadsDirty) {
        try { await api.put(`${base}/yard-map/roads`, { roads: roadsToGeoJson(roads!) }); roadsSaved = true; }
        catch (e) { failures.push(`${t('map.roads')}: ${messageOf(e, t('common.offline'))}`); }
      }
    } finally {
      setSaving(false);
    }
    if (failures.length === 0) {
      setDone(t('map.saved'));
      setSelected(null);
      setLoadTick(n => n + 1);
      return;
    }
    setError(failures.join('\n'));
    setRemoved(stillRemoved);
    setAreas(list => list!.map(a => (savedKeys.has(a.key) ? { ...a, id: savedKeys.get(a.key)!, dirty: false } : a)));
    if (roadsSaved) setRoadsDirty(false);
  }

  function discard() {
    setSelected(null);
    setDrawing([]);
    setLoadTick(n => n + 1);
  }

  async function importFile(file: File) {
    try {
      const parsed = importGeoJson(JSON.parse(await file.text()));
      if (parsed.areas.length === 0 && parsed.roads.length === 0) { setError(t('map.importNothing')); return; }
      setAreas(list => [...list!, ...parsed.areas.map(a => ({ ...a, key: newKey(), id: null, dirty: true }))]);
      if (parsed.roads.length > 0) updateRoads([...roads!, ...parsed.roads]);
      setDone(t('map.imported', { areas: parsed.areas.length, roads: parsed.roads.length, skipped: parsed.skipped }));
      if (size) setView(fitView([...parsed.areas.flatMap(a => a.ring), ...parsed.roads.flatMap(r => r.points)], size.width, size.height));
    } catch {
      setError(t('map.importBad'));
    }
  }

  // ---------------------------------------------------------------- render

  if (loadError) return <section className="screen"><header className="screen-head"><h1>{t('map.title')}</h1></header><LoadError error={loadError} retry={() => setLoadTick(n => n + 1)} /></section>;
  if (!areas || !roads) return <section className="screen"><header className="screen-head"><h1>{t('map.title')}</h1></header><p className="muted">{t('common.loading')}</p></section>;

  const selArea = selected?.type === 'area' ? areas.find(a => a.key === selected.key) ?? null : null;
  const roadIndex = selected?.type === 'road' ? selected.index : -1;
  const selRoad = roadIndex >= 0 ? roads[roadIndex] ?? null : null;
  const sorted = [...areas].sort((a, b) => ringSize(b.ring) - ringSize(a.ring));
  const changes = removed.length + areas.filter(a => a.dirty).length + (roadsDirty ? 1 : 0);

  return (
    <section className="screen map-screen">
      <header className="screen-head">
        <h1>{t('map.title')}</h1>
        <p className="head-figure">{t('map.lead')}</p>
        {canEdit && (
          <div className="map-save">
            {dirty && <span className="muted">{t('map.unsaved', { n: changes })}</span>}
            <button type="button" className="btn btn-secondary" disabled={!dirty || saving} onClick={discard}>{t('map.discard')}</button>
            <button type="button" className="btn btn-primary" disabled={!dirty || saving} aria-busy={saving} onClick={save}>{t('map.save')}</button>
          </div>
        )}
      </header>

      {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
      {error && <Notice kind="error" onClose={() => setError(null)}><span className="pre-line">{error}</span></Notice>}

      <div className="map-grid">
        <div className="map-stage">
          <div className="map-tools" role="toolbar" aria-label={t('map.tools')}>
            <ToolButton tool="select" current={tool} onPick={chooseTool} label={t('map.toolSelect')} />
            {canEdit && <ToolButton tool="area" current={tool} onPick={chooseTool} label={t('map.toolArea')} />}
            {canEdit && <ToolButton tool="road" current={tool} onPick={chooseTool} label={t('map.toolRoad')} />}
            <ToolButton tool="route" current={tool} onPick={chooseTool} label={t('map.toolRoute')} />
          </div>

          <div className="map-box" ref={boxRef} data-tool={tool}>
            {view && (
              <svg ref={svgRef} width={view.width} height={view.height} className="map-svg"
                onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerUp}
                onPointerLeave={() => setCursor(null)} onDoubleClick={onDoubleClick} role="application" aria-label={t('map.title')}>
                <defs>
                  <marker id="oneway" viewBox="0 0 10 10" refX="5" refY="5" markerWidth="13" markerHeight="13" markerUnits="userSpaceOnUse" orient="auto">
                    <path d="M1 1 L9 5 L1 9 z" className="map-arrow" />
                  </marker>
                </defs>
                <Grid view={view} />
                {tiles && showTiles && <TileLayer view={view} template={tiles} />}

                {sorted.map(a => <AreaShapeView key={a.key} view={view} area={a} selected={selArea?.key === a.key} />)}

                <g className="map-roads">
                  {roads.map((r, i) => <polyline key={`c${i}`} className="road-casing" points={pts(view, r.points)} />)}
                  {roads.map((r, i) => (
                    <polyline key={`r${i}`} className={selected?.type === 'road' && selected.index === i ? 'road-line road-selected' : 'road-line'} points={pts(view, r.points)} />
                  ))}
                  {roads.map((r, i) => r.oneWay && (
                    // Arrows at every corner and halfway along every stretch, pointing the drawn direction.
                    <polyline key={`a${i}`} className="road-arrow-carrier" points={pts(view, withMidpoints(r.points))} markerMid="url(#oneway)" />
                  ))}
                </g>

                {sorted.map(a => <AreaLabel key={`l${a.key}`} view={view} area={a} label={a.zoneType === 'YARD_ZONE' ? (a.yardBlock || '?') : tx('zone', a.zoneType)} />)}

                {route && <polyline className="route-line" points={pts(view, route.path)} />}
                {routeEnds.map((p, i) => {
                  const [x, y] = toScreen(view, p);
                  return <g key={i} className="route-end" transform={`translate(${x} ${y})`}><circle r="9" /><text dy="4">{i === 0 ? 'A' : 'B'}</text></g>;
                })}

                {drawing.length > 0 && (
                  <g className="drawing">
                    <polyline points={pts(view, cursor ? [...drawing, cursor] : drawing)} className={tool === 'area' ? 'drawing-area' : 'drawing-road'} />
                    {drawing.map((p, i) => { const [x, y] = toScreen(view, p); return <circle key={i} cx={x} cy={y} r={i === 0 ? 5 : 3.5} />; })}
                  </g>
                )}

                {canEdit && selArea && <Handles view={view} points={selArea.ring} closed />}
                {canEdit && selRoad && <Handles view={view} points={selRoad.points} closed={false} />}
              </svg>
            )}

            <div className="map-zoom">
              <button type="button" className="icon-btn" aria-label={t('map.zoomIn')} onClick={() => setView(v => v && zoomAt(v, v.width / 2, v.height / 2, 1.5))}>+</button>
              <button type="button" className="icon-btn" aria-label={t('map.zoomOut')} onClick={() => setView(v => v && zoomAt(v, v.width / 2, v.height / 2, 1 / 1.5))}>−</button>
              <button type="button" className="icon-btn map-fit" onClick={() => size && setView(fitView(framePoints(), size.width, size.height))}>{t('map.fit')}</button>
            </div>
            {view && <ScaleBar view={view} />}
            {tiles && (
              <div className="map-attrib">
                <label className="map-tiles-toggle"><input type="checkbox" checked={showTiles} onChange={e => setShowTiles(e.target.checked)} /> {t('map.background')}</label>
                {showTiles && attribution && <span>{attribution}</span>}
              </div>
            )}
          </div>
          <p className="hint map-hint">{hintFor(tool, drawing.length, t)}</p>
        </div>

        <aside className="map-side">
          {selArea && (
            <SideBlock title={t('map.area')}>
              <label className="field"><span>{t('map.name')}</span>
                <input value={selArea.name} disabled={!canEdit} maxLength={100} onChange={e => updateArea(selArea.key, { name: e.target.value })} /></label>
              <label className="field"><span>{t('map.kind')}</span>
                <select value={selArea.zoneType} disabled={!canEdit} onChange={e => updateArea(selArea.key, { zoneType: e.target.value })}>
                  {ZONE_TYPES.map(z => <option key={z} value={z}>{tx('zone', z)}</option>)}
                </select></label>
              {(selArea.zoneType === 'YARD_ZONE' || selArea.zoneType === 'CFS') && (
                <label className="field"><span>{t('map.block')}</span>
                  <input value={selArea.yardBlock ?? ''} dir="ltr" disabled={!canEdit} maxLength={20} className="field-short"
                    onChange={e => updateArea(selArea.key, { yardBlock: e.target.value.toUpperCase() })} />
                  <span className="hint">{t('map.blockHint')}</span></label>
              )}
              <label className="radio"><input type="checkbox" checked={selArea.triggersCheckIn} disabled={!canEdit}
                onChange={e => updateArea(selArea.key, { triggersCheckIn: e.target.checked })} /> {t('map.checkIn')}</label>
              <p className="hint">{t('map.areaSize', { m: Math.round(ringSize(selArea.ring)).toLocaleString() })}</p>
              {canEdit && <button type="button" className="btn btn-danger-quiet" onClick={removeSelected}>{t('map.removeArea')}</button>}
            </SideBlock>
          )}

          {selRoad && (
            <SideBlock title={t('map.road')}>
              <label className="field"><span>{t('map.name')}</span>
                <input value={selRoad.name} disabled={!canEdit} maxLength={60} placeholder={t('map.roadNameHint')}
                  onChange={e => updateRoads(roads.map((r, i) => (i === roadIndex ? { ...r, name: e.target.value } : r)))} /></label>
              <label className="radio"><input type="checkbox" checked={selRoad.oneWay} disabled={!canEdit}
                onChange={e => updateRoads(roads.map((r, i) => (i === roadIndex ? { ...r, oneWay: e.target.checked } : r)))} /> {t('map.oneWay')}</label>
              {canEdit && selRoad.oneWay && (
                <button type="button" className="btn btn-secondary"
                  onClick={() => updateRoads(roads.map((r, i) => (i === roadIndex ? { ...r, points: [...r.points].reverse() } : r)))}>{t('map.reverse')}</button>
              )}
              <p className="hint">{t('map.length', { m: Math.round(pathMeters(selRoad.points)).toLocaleString() })}</p>
              {canEdit && <button type="button" className="btn btn-danger-quiet" onClick={removeSelected}>{t('map.removeRoad')}</button>}
            </SideBlock>
          )}

          {tool === 'route' && (
            <SideBlock title={t('map.toolRoute')}>
              {routeEnds.length < 2 ? <p className="muted">{t('map.routeHint')}</p>
                : route ? <p className="route-figure"><span className="num">{Math.round(route.meters).toLocaleString()}</span> {t('map.meters')}</p>
                : <p className="check-fail">{t('map.noRoute')}</p>}
            </SideBlock>
          )}

          <SideBlock title={t('map.check')}>
            {problems.length === 0
              ? <p className="map-ok"><span className="check-mark" aria-hidden="true" /> {t('map.ready')}</p>
              : <ul className="checks">{problems.map((p, i) => <li key={i} className="check check-fail"><span className="check-mark" aria-hidden="true" />{problemText(p, t)}</li>)}</ul>}
          </SideBlock>

          <SideBlock title={`${t('map.areas')} (${areas.length})`}>
            <ul className="map-list">
              {[...areas].sort((a, b) => ZONE_TYPES.indexOf(a.zoneType as never) - ZONE_TYPES.indexOf(b.zoneType as never) || a.name.localeCompare(b.name)).map(a => (
                <li key={a.key}>
                  <button type="button" className={selArea?.key === a.key ? 'map-item active' : 'map-item'}
                    onClick={() => { chooseTool('select'); setSelected({ type: 'area', key: a.key }); }}>
                    <span className={`zone-swatch zone-${a.zoneType.toLowerCase()}`} aria-hidden="true" />
                    <span className="map-item-name" dir="auto">{a.name}</span>
                    <span className="muted">{a.zoneType === 'YARD_ZONE' && a.yardBlock ? <bdi>{a.yardBlock}</bdi> : tx('zone', a.zoneType)}</span>
                    {a.dirty && <span className="map-dot" title={t('map.changed')} />}
                  </button>
                </li>
              ))}
            </ul>
          </SideBlock>

          <SideBlock title={`${t('map.roads')} (${roads.length})`}>
            <ul className="map-list">
              {roads.map((r, i) => (
                <li key={i}>
                  <button type="button" className={selected?.type === 'road' && selected.index === i ? 'map-item active' : 'map-item'}
                    onClick={() => { chooseTool('select'); setSelected({ type: 'road', index: i }); }}>
                    <span className="road-swatch" aria-hidden="true" />
                    <span className="map-item-name" dir="auto">{r.name || t('map.roadN', { n: i + 1 })}</span>
                    <span className="muted num">{Math.round(pathMeters(r.points)).toLocaleString()} {t('map.m')}{r.oneWay ? ` · ${t('map.oneWayShort')}` : ''}</span>
                  </button>
                </li>
              ))}
            </ul>
          </SideBlock>

          {canEdit && (
            <SideBlock title={t('map.import')}>
              <p className="hint">{t('map.importHint')}</p>
              <input ref={fileRef} type="file" accept=".geojson,.json,application/geo+json,application/json" hidden
                onChange={e => { const f = e.target.files?.[0]; if (f) void importFile(f); e.target.value = ''; }} />
              <button type="button" className="btn btn-secondary" onClick={() => fileRef.current?.click()}>{t('map.importButton')}</button>
            </SideBlock>
          )}
        </aside>
      </div>
    </section>
  );
}

// ------------------------------------------------------------------ pieces

function ToolButton({ tool, current, onPick, label }: { tool: Tool; current: Tool; onPick: (t: Tool) => void; label: string }) {
  const icons: Record<Tool, ReactNode> = {
    select: <path d="M5 3l12 8-5 1.2L14.5 19l-2.4 1-2.6-6.7L5 16z" />,
    area: <><path d="M4 7l7-3 9 5-3 10-11-2z" /><circle cx="4" cy="7" r="1.6" /><circle cx="20" cy="9" r="1.6" /><circle cx="17" cy="19" r="1.6" /></>,
    road: <><path d="M3 19C8 15 9 8 14 7s6 1 7-2" /><path d="M3 19C8 15 9 8 14 7s6 1 7-2" strokeDasharray="2 3" stroke="#fff" /></>,
    route: <><circle cx="5" cy="18" r="2.2" /><circle cx="19" cy="6" r="2.2" /><path d="M7 17c6-1 2-7 6-9s3-1 4-1" /></>,
  };
  return (
    <button type="button" className={tool === current ? 'map-tool active' : 'map-tool'} aria-pressed={tool === current} onClick={() => onPick(tool)}>
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{icons[tool]}</svg>
      <span>{label}</span>
    </button>
  );
}

function SideBlock({ title, children }: { title: string; children: ReactNode }) {
  return <section className="map-side-block"><h2>{title}</h2>{children}</section>;
}

function AreaShapeView({ view, area, selected }: { view: View; area: DraftArea; selected: boolean }) {
  if (area.ring.length < 3) return null;
  return (
    <g className={`zone zone-${area.zoneType.toLowerCase()}${selected ? ' zone-selected' : ''}`}>
      <polygon points={pts(view, area.ring)} />
    </g>
  );
}

/** Block names and area kinds, drawn above the roads; left out when the area is too small on screen to hold them. */
function AreaLabel({ view, area, label }: { view: View; area: DraftArea; label: string }) {
  if (area.ring.length < 3 || area.zoneType === 'APPROACH') return null;
  const screen = area.ring.map(p => toScreen(view, p));
  const xs = screen.map(p => p[0]);
  const ys = screen.map(p => p[1]);
  const w = Math.max(...xs) - Math.min(...xs);
  const h = Math.max(...ys) - Math.min(...ys);
  if (w <= 30 || h <= 16) return null;
  const [cx, cy] = toScreen(view, centroid(area.ring));
  const big = area.zoneType === 'YARD_ZONE' && w > 46 && h > 26;
  return (
    <text x={cx} y={cy} className={`zone-label zone-label-${area.zoneType.toLowerCase()}${big ? ' zone-label-big' : ''}`}
      dominantBaseline="central" textAnchor="middle">{label}</text>
  );
}

function Handles({ view, points, closed }: { view: View; points: LonLat[]; closed: boolean }) {
  const segments = closed ? points.length : points.length - 1;
  return (
    <g className="handles">
      {Array.from({ length: segments }, (_, i) => {
        const [x, y] = toScreen(view, midpoint(points[i], points[(i + 1) % points.length]));
        return <circle key={`m${i}`} data-handle="mid" data-index={i} cx={x} cy={y} r="4" className="handle-mid" />;
      })}
      {points.map((p, i) => {
        const [x, y] = toScreen(view, p);
        return <rect key={`v${i}`} data-handle="vertex" data-index={i} x={x - 5} y={y - 5} width="10" height="10" className="handle" />;
      })}
    </g>
  );
}

/** Background map tiles (Web Mercator, {z}/{x}/{y}); stretched beyond zoom 19. */
function TileLayer({ view, template }: { view: View; template: string }) {
  const z = Math.max(0, Math.min(19, Math.floor(Math.log2(view.scale))));
  const tileWorld = 256 / 2 ** z;
  const px = tileWorld * view.scale;
  const x0 = Math.floor((view.cx - view.width / 2 / view.scale) / tileWorld);
  const x1 = Math.floor((view.cx + view.width / 2 / view.scale) / tileWorld);
  const y0 = Math.max(0, Math.floor((view.cy - view.height / 2 / view.scale) / tileWorld));
  const y1 = Math.min(2 ** z - 1, Math.floor((view.cy + view.height / 2 / view.scale) / tileWorld));
  const images: ReactNode[] = [];
  for (let x = x0; x <= x1 && images.length < 120; x++) {
    for (let y = y0; y <= y1 && images.length < 120; y++) {
      const wrapped = ((x % 2 ** z) + 2 ** z) % 2 ** z;
      const href = template.replace('{z}', String(z)).replace('{x}', String(wrapped)).replace('{y}', String(y));
      images.push(<image key={`${z}/${x}/${y}`} href={href} x={(x * tileWorld - view.cx) * view.scale + view.width / 2}
        y={(y * tileWorld - view.cy) * view.scale + view.height / 2} width={px + 0.5} height={px + 0.5} preserveAspectRatio="none" />);
    }
  }
  return <g className="map-tiles">{images}</g>;
}

/** Plain plan background: a grid every 100 m (or 50 / 500 m to keep it readable). */
function Grid({ view }: { view: View }) {
  const mpp = metersPerPixel(view);
  const step = [10, 25, 50, 100, 250, 500, 1000, 2500].find(s => s / mpp >= 60) ?? 5000;
  const px = step / mpp;
  const ox = ((view.width / 2 - view.cx * view.scale) % px + px) % px;
  const oy = ((view.height / 2 - view.cy * view.scale) % px + px) % px;
  const lines: ReactNode[] = [];
  for (let x = ox; x < view.width; x += px) lines.push(<line key={`x${x}`} x1={x} x2={x} y1={0} y2={view.height} />);
  for (let y = oy; y < view.height; y += px) lines.push(<line key={`y${y}`} y1={y} y2={y} x1={0} x2={view.width} />);
  return <g className="map-grid-lines">{lines}</g>;
}

function ScaleBar({ view }: { view: View }) {
  const mpp = metersPerPixel(view);
  const meters = [5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000].find(m => m / mpp >= 70) ?? 10000;
  return (
    <div className="map-scale" aria-hidden="true">
      <span style={{ inlineSize: `${meters / mpp}px` }} />
      <bdi>{meters >= 1000 ? `${meters / 1000} km` : `${meters} m`}</bdi>
    </div>
  );
}

// ------------------------------------------------------------------ helpers

const pts = (view: View, points: LonLat[]) => points.map(p => toScreen(view, p).map(n => n.toFixed(1)).join(',')).join(' ');

const withMidpoints = (points: LonLat[]): LonLat[] =>
  points.flatMap((p, i) => (i === 0 ? [p] : [midpoint(points[i - 1], p), p]));

const midpoint = (a: LonLat, b: LonLat): LonLat => round6([(a[0] + b[0]) / 2, (a[1] + b[1]) / 2]);

/** Approximate area in square metres (shoelace on Web Mercator, corrected for latitude). */
function ringSize(ring: LonLat[]): number {
  if (ring.length < 3) return 0;
  const world = ring.map(toWorld);
  let sum = 0;
  for (let i = 0, j = world.length - 1; i < world.length; j = i++) sum += world[j][0] * world[i][1] - world[i][0] * world[j][1];
  const lat = centroid(ring)[1];
  const metersPerWorld = (40075016.686 * Math.cos((lat * Math.PI) / 180)) / 256;
  return Math.abs(sum / 2) * metersPerWorld * metersPerWorld;
}

function hintFor(tool: Tool, points: number, t: (k: TextKey) => string): string {
  if (tool === 'area') return points === 0 ? t('map.hintArea') : t('map.hintAreaMore');
  if (tool === 'road') return points === 0 ? t('map.hintRoad') : t('map.hintRoadMore');
  if (tool === 'route') return t('map.routeHint');
  return t('map.hintSelect');
}

function problemText(p: Problem, t: (k: TextKey, v?: Record<string, string | number>) => string): string {
  switch (p.kind) {
    case 'noRoads': return t('map.pNoRoads');
    case 'noGate': return t('map.pNoGate');
    case 'noExit': return t('map.pNoExit');
    case 'pieces': return t('map.pPieces', { n: p.count });
    case 'noBlock': return t('map.pNoBlock', { name: p.area });
    case 'unreachable': return t('map.pUnreachable', { block: p.block });
    case 'noWayOut': return t('map.pNoWayOut', { block: p.block });
    case 'duplicateBlock': return t('map.pDuplicate', { block: p.block });
  }
}
