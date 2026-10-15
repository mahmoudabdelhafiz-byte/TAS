import { useEffect, useMemo, useRef, useState } from 'react';
import { centroid, fitView, toScreen, type LonLat, type View } from '../lib/geo.ts';
import { clock, duration } from '../lib/time.ts';
import type { ControlRoomBoard, ControlStage, ControlStop, ControlTruck, YardMapArea } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { LoadError, useLoad } from '../ui/kit.tsx';

const STAGE_ORDER: ControlStage[] = ['TO_BLOCK', 'AT_BLOCK', 'TO_EXIT', 'AT_GATE', 'ON_THE_WAY'];

/** Where to draw a truck: its last app position, else the block it reached. */
function markerPoint(truck: ControlTruck, areas: YardMapArea[]): LonLat | null {
  if (truck.position) return truck.position;
  if (truck.stage !== 'AT_BLOCK') return null;
  const stop = truck.stops.find(s => s.sequence === truck.currentStop);
  const area = stop?.block ? areas.find(a => a.block?.toUpperCase() === stop.block!.toUpperCase()) : undefined;
  return area ? centroid(area.ring) : null;
}

export function ControlRoom() {
  const { api, t, tx, terminal } = useApp();
  const board = useLoad(() => api.get<ControlRoomBoard>(`/api/terminals/${terminal.terminalId}/control-room`), [terminal.terminalId], 15_000);
  const [selected, setSelected] = useState<string | null>(null);
  const min = t('unit.min');
  const hour = t('unit.h');
  const b = board.data;

  const inside = b?.trucks.filter(x => x.status === 'GateIn') ?? [];
  const outside = b?.trucks.filter(x => x.status !== 'GateIn') ?? [];
  const chosen = b?.trucks.find(x => x.appointmentNo === selected) ?? null;
  const minutes = (m: number | null) => (m === null ? '—' : duration(Math.round(m), min, hour));

  return (
    <section className="screen control">
      <header className="screen-head">
        <h1>{t('control.title')}</h1>
        {b && (
          <dl className="figures">
            <div><dt>{t('control.onTheWay')}</dt><dd className="num">{b.stats.onTheWay}</dd></div>
            <div><dt>{t('gate.atGate')}</dt><dd className="num">{b.stats.atGate}</dd></div>
            <div><dt>{t('control.inside')}</dt><dd className="num">{b.stats.inside}</dd></div>
            <div><dt>{t('control.doneToday')}</dt><dd className="num">{b.stats.completedToday}</dd></div>
            <div><dt>{t('control.avgInside')}</dt><dd className="num">{minutes(b.stats.avgMinutesInside)}</dd></div>
            <div><dt>{t('control.avgToBlock')}</dt><dd className="num">{minutes(b.stats.avgMinutesToBlock)}</dd></div>
            <div><dt>{t('control.avgAtBlock')}</dt><dd className="num">{minutes(b.stats.avgMinutesAtBlock)}</dd></div>
          </dl>
        )}
      </header>

      {board.error && <LoadError error={board.error} retry={board.reload} />}
      {!b && !board.error && <p className="muted">{t('common.loading')}</p>}

      {b && (
        <div className="control-grid">
          <div className="control-map-col">
            <YardMap board={b} selected={selected} onSelect={setSelected} />
            <ul className="legend control-legend">
              {STAGE_ORDER.filter(s => s !== 'ON_THE_WAY' && s !== 'AT_GATE').map(s => (
                <li key={s}><span className={`truck-dot stage-${s.toLowerCase()}`} aria-hidden="true" />{tx('control.stage', s)}</li>
              ))}
              <li><span className="truck-dot overdue" aria-hidden="true" />{t('control.overdue', { m: b.turnTargetMinutes })}</li>
            </ul>
            {chosen && <TruckDetail truck={chosen} onClose={() => setSelected(null)} />}
          </div>

          <div className="control-lists">
            <h2 className="section-title control-first">{t('control.insideTitle')} <span className="muted num">{inside.length}</span></h2>
            {inside.length === 0 ? <p className="muted">{t('control.noneInside')}</p> : (
              <ul className="truck-list">
                {inside.map(x => <TruckRow key={x.appointmentNo} truck={x} target={b.turnTargetMinutes} selected={selected === x.appointmentNo} onSelect={setSelected} />)}
              </ul>
            )}

            <h2 className="section-title">{t('control.comingTitle')} <span className="muted num">{outside.length}</span></h2>
            {outside.length === 0 ? <p className="muted">{t('control.noneComing')}</p> : (
              <ul className="truck-list">
                {outside.map(x => <TruckRow key={x.appointmentNo} truck={x} target={b.turnTargetMinutes} selected={selected === x.appointmentNo} onSelect={setSelected} />)}
              </ul>
            )}
          </div>
        </div>
      )}
    </section>
  );
}

function stopLabel(stop: ControlStop | undefined, t: ReturnType<typeof useApp>['t']): string {
  if (!stop) return t('control.toExit');
  if (!stop.block) return t('control.noPosition');
  return stop.slot ? `${stop.block} · ${stop.slot}` : stop.block;
}

function TruckRow({ truck, target, selected, onSelect }: { truck: ControlTruck; target: number; selected: boolean; onSelect: (no: string | null) => void }) {
  const { t, tx, terminal } = useApp();
  const stop = truck.stops.find(s => s.sequence === truck.currentStop);
  const inside = truck.status === 'GateIn';
  return (
    <li>
      <button type="button" className={selected ? 'truck-row active' : 'truck-row'} aria-pressed={selected}
        onClick={() => onSelect(selected ? null : truck.appointmentNo)}>
        <span className={`truck-dot stage-${truck.stage.toLowerCase()}${truck.overdue ? ' overdue' : ''}`} aria-hidden="true" />
        <span className="truck-main">
          <span className="truck-plate plate"><bdi>{truck.truckPlate ?? truck.appointmentNo}</bdi></span>
          <span className="truck-company" dir="auto">{truck.company}</span>
        </span>
        <span className="truck-where">
          <span className="truck-stage">{tx('control.stage', truck.stage)}</span>
          {inside && <span className="truck-stop num"><bdi>{stopLabel(stop, t)}</bdi></span>}
          {!inside && truck.eta && <span className="truck-stop num">{t('control.eta', { time: clock(truck.eta, terminal.timeZone) })}</span>}
          {!inside && !truck.eta && <span className="truck-stop num">{clock(truck.slotStart, terminal.timeZone)}</span>}
        </span>
        <span className={truck.overdue ? 'truck-time overdue-text num' : 'truck-time num'}>
          {truck.minutesInside !== null ? duration(truck.minutesInside, t('unit.min'), t('unit.h')) : ''}
          {truck.silent && <span className="truck-silent" title={t('control.silentHint')}>{t('control.silent')}</span>}
          {truck.overdue && <span className="visually-hidden">{t('control.overdue', { m: target })}</span>}
        </span>
      </button>
    </li>
  );
}

function TruckDetail({ truck, onClose }: { truck: ControlTruck; onClose: () => void }) {
  const { t, terminal, lang } = useApp();
  const at = (iso: string | null) => (iso ? clock(iso, terminal.timeZone) : '—');
  return (
    <section className="truck-detail" aria-label={truck.appointmentNo}>
      <header className="truck-detail-head">
        <div>
          <h2><bdi>{truck.truckPlate ?? '—'}</bdi> <span className="muted">· <bdi>{truck.appointmentNo}</bdi></span></h2>
          <p className="muted" dir="auto">{truck.company}{truck.driverName ? ` · ${truck.driverName}` : ''}</p>
        </div>
        <button type="button" className="link-btn" onClick={onClose}>{t('common.close')}</button>
      </header>
      <dl className="facts">
        <dt>{t('control.moves')}</dt><dd><bdi>{truck.moves.join('  ')}</bdi></dd>
        <dt>{t('control.slot')}</dt><dd className="num">{at(truck.slotStart)}–{at(truck.slotEnd)}</dd>
        {truck.arrivedAt && <><dt>{t('gate.atGate')}</dt><dd className="num">{at(truck.arrivedAt)}</dd></>}
        {truck.gateInAt && <><dt>{t('control.gateIn')}</dt><dd className="num">{at(truck.gateInAt)}</dd></>}
        {truck.positionAt && <><dt>{t('control.lastSeen')}</dt><dd className="num">{at(truck.positionAt)}</dd></>}
      </dl>
      {truck.stops.length > 0 && (
        <ol className="stop-steps">
          {truck.stops.map(s => (
            <li key={s.sequence} className={`stop-step stop-${s.status.toLowerCase()}${s.sequence === truck.currentStop ? ' stop-current' : ''}`}>
              <span className="stop-name"><bdi>{s.stopType === 'EXIT' ? t('control.exit') : stopLabel(s, t)}</bdi></span>
              <span className="stop-times num">
                {s.arrivedAt && <>{t('control.reached', { time: at(s.arrivedAt) })}</>}
                {s.completedAt && <>{lang === 'ar' ? '، ' : ', '}{t('control.done', { time: at(s.completedAt) })}</>}
              </span>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}

/** The terminal plan with the trucks inside: plate tags at their last position, target blocks outlined. */
function YardMap({ board, selected, onSelect }: { board: ControlRoomBoard; selected: string | null; onSelect: (no: string | null) => void }) {
  const { tx } = useApp();
  const box = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState<{ width: number; height: number } | null>(null);
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    const observer = new ResizeObserver(entries => {
      const r = entries[0].contentRect;
      setSize({ width: Math.round(r.width), height: Math.round(r.height) });
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const { areas, roads } = board.map;
  const view: View | null = useMemo(() => {
    if (!size) return null;
    const pts = [...areas.filter(a => a.kind !== 'APPROACH').flatMap(a => a.ring), ...roads.flat()] as LonLat[];
    return fitView(pts, size.width, size.height);
  }, [size, areas, roads]);

  const targets = new Set(board.trucks
    .filter(x => x.status === 'GateIn')
    .map(x => x.stops.find(s => s.sequence === x.currentStop)?.block?.toUpperCase())
    .filter((b): b is string => !!b));
  const pts = (v: View, list: LonLat[]) => list.map(p => toScreen(v, p).map(n => n.toFixed(1)).join(',')).join(' ');

  return (
    <div className="control-map" ref={box}>
      {view && (
        <svg width={view.width} height={view.height} role="img" aria-label="Yard">
          {areas.map((a, i) => {
            const target = a.block && targets.has(a.block.toUpperCase());
            const [cx, cy] = toScreen(view, centroid(a.ring));
            return (
              <g key={i} className={`zone zone-${a.kind.toLowerCase()}${target ? ' zone-target' : ''}`}>
                <polygon points={pts(view, a.ring)} />
                <text x={cx} y={cy} className={`zone-label zone-label-${a.kind.toLowerCase()}`} dominantBaseline="central" textAnchor="middle">
                  {a.kind === 'YARD_ZONE' ? a.block : a.kind === 'GATE' || a.kind === 'EXIT' ? tx('zone', a.kind) : ''}
                </text>
              </g>
            );
          })}
          <g className="map-roads">
            {roads.map((r, i) => <polyline key={`c${i}`} className="road-casing thin" points={pts(view, r)} />)}
            {roads.map((r, i) => <polyline key={`r${i}`} className="road-line thin" points={pts(view, r)} />)}
          </g>
          {board.trucks.filter(x => x.status === 'GateIn').map(x => {
            const p = markerPoint(x, areas);
            if (!p) return null;
            const [sx, sy] = toScreen(view, p);
            const label = x.truckPlate ?? x.appointmentNo;
            const width = Math.max(44, label.length * 7.4 + 16);
            return (
              <g key={x.appointmentNo} className={`truck-marker stage-${x.stage.toLowerCase()}${x.overdue ? ' overdue' : ''}${selected === x.appointmentNo ? ' active' : ''}${x.silent ? ' silent' : ''}`}
                transform={`translate(${sx.toFixed(1)} ${sy.toFixed(1)})`} onClick={() => onSelect(selected === x.appointmentNo ? null : x.appointmentNo)}>
                <circle r="6" />
                <rect x={9} y={-11} width={width} height={22} rx={3} />
                <text x={9 + width / 2} y={0} dominantBaseline="central" textAnchor="middle">{label}</text>
              </g>
            );
          })}
        </svg>
      )}
    </div>
  );
}
