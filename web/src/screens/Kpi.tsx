import { useEffect, useRef, useState, type RefObject } from 'react';
import { addDays, dayLabel, duration } from '../lib/time.ts';
import { useApp } from '../ui/context.tsx';
import { LoadError, useLoad } from '../ui/kit.tsx';

interface KpiDay {
  date: string;
  capacity: number;
  booked: number;
  utilization: number | null;
  appointments: number;
  completed: number;
  noShows: number;
  cancelled: number;
  walkIns: number;
  arrived: number;
  onTime: number;
  avgTurnMinutes: number | null;
  avgWaitMinutes: number | null;
}

interface KpiHour {
  hour: number;
  capacity: number;
  booked: number;
  utilization: number | null;
  appointments: number;
  avgTurnMinutes: number | null;
}

interface KpiReport {
  from: string;
  to: string;
  totals: {
    capacity: number;
    booked: number;
    utilization: number | null;
    appointments: number;
    completed: number;
    noShows: number;
    cancelled: number;
    walkIns: number;
    noShowRate: number | null;
    onTimeRate: number | null;
    walkInShare: number | null;
    avgTurnMinutes: number | null;
    avgWaitMinutes: number | null;
  };
  days: KpiDay[];
  hours: KpiHour[];
  yard: KpiYard | null;
}

/** Times inside the terminal from the driver app (null when the terminal has no yard stops in the range). */
interface KpiYard {
  visits: number;
  stops: number;
  appSeenShare: number | null;
  avgToBlockMinutes: number | null;
  avgAtBlockMinutes: number | null;
  avgToExitMinutes: number | null;
  days: { date: string; visits: number; avgToBlockMinutes: number | null; avgAtBlockMinutes: number | null }[];
  blocks: { block: string; stops: number; avgDriveMinutes: number | null; avgAtBlockMinutes: number | null }[];
}

/** "16 Sep" — day and month, no weekday, for axis labels. */
const shortDay = (date: string, lang: string) => {
  const [y, m, d] = date.split('-').map(Number);
  return new Intl.DateTimeFormat(lang === 'ar' ? 'ar-u-nu-latn' : 'en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' })
    .format(new Date(Date.UTC(y, m - 1, d)));
};

const pct = (v: number | null) => (v === null ? '—' : `${Math.round(v * 100)}%`);

export function Kpi() {
  const { api, t, lang, terminal, today } = useApp();
  const [range, setRange] = useState<{ from: string; to: string }>({ from: addDays(today, -6), to: today });
  const report = useLoad(
    () => api.get<KpiReport>(`/api/terminals/${terminal.terminalId}/kpis?from=${range.from}&to=${range.to}`),
    [terminal.terminalId, range.from, range.to]);
  const [table, setTable] = useState(false);
  const min = t('unit.min');
  const hour = t('unit.h');

  const presets: { label: string; from: string; to: string }[] = [
    { label: t('kpi.last7'), from: addDays(today, -6), to: today },
    { label: t('kpi.last30'), from: addDays(today, -29), to: today },
    { label: t('kpi.next7'), from: today, to: addDays(today, 6) },
  ];
  const r = report.data;

  return (
    <section className="screen kpi">
      <header className="screen-head">
        <h1>{t('kpi.title')}</h1>
        <div className="segmented">
          {presets.map(p => (
            <button key={p.label} type="button" className={range.from === p.from && range.to === p.to ? 'seg active' : 'seg'}
              onClick={() => setRange({ from: p.from, to: p.to })}>{p.label}</button>
          ))}
        </div>
        <div className="range">
          <label className="field field-inline"><span>{t('setup.from')}</span>
            <input type="date" value={range.from} max={range.to} onChange={e => e.target.value && setRange(x => ({ ...x, from: e.target.value }))} /></label>
          <label className="field field-inline"><span>{t('setup.to')}</span>
            <input type="date" value={range.to} min={range.from} onChange={e => e.target.value && setRange(x => ({ ...x, to: e.target.value }))} /></label>
        </div>
      </header>

      {report.error && <LoadError error={report.error} retry={report.reload} />}

      {r && (
        <>
          <dl className="tiles">
            <Tile label={t('kpi.slotUse')} value={pct(r.totals.utilization)} note={t('kpi.slotUseNote', { b: r.totals.booked, c: r.totals.capacity })} />
            <Tile label={t('kpi.appointments')} value={String(r.totals.appointments)} note={t('kpi.appointmentsNote', { done: r.totals.completed, cancelled: r.totals.cancelled })} />
            <Tile label={t('kpi.noShow')} value={pct(r.totals.noShowRate)} note={t('kpi.noShowNote', { n: r.totals.noShows })} />
            <Tile label={t('kpi.onTime')} value={pct(r.totals.onTimeRate)} note={t('kpi.onTimeNote')} />
            <Tile label={t('kpi.turn')} value={r.totals.avgTurnMinutes === null ? '—' : duration(r.totals.avgTurnMinutes, min, hour)}
              note={r.totals.avgWaitMinutes === null ? t('kpi.turnNote') : t('kpi.waitNote', { wait: duration(r.totals.avgWaitMinutes, min, hour) })} />
            <Tile label={t('kpi.walkIns')} value={pct(r.totals.walkInShare)} note={t('kpi.walkInsNote', { n: r.totals.walkIns })} />
          </dl>

          <div className="charts">
            <figure className="chart-card">
              <figcaption>{t('kpi.chartDaily')}</figcaption>
              <Columns
                bars={r.days.map(d => ({
                  key: d.date,
                  label: shortDay(d.date, lang),
                  value: d.utilization,
                  tip: `${dayLabel(d.date, lang)}: ${pct(d.utilization)} (${t('kpi.slotUseNote', { b: d.booked, c: d.capacity })})`,
                }))}
                max={1} format={v => `${Math.round(v * 100)}%`} />
            </figure>
            <figure className="chart-card">
              <figcaption>{t('kpi.chartTurn')}</figcaption>
              <Line
                points={r.days.map(d => ({
                  key: d.date,
                  label: shortDay(d.date, lang),
                  value: d.avgTurnMinutes,
                  tip: `${dayLabel(d.date, lang)}: ${d.avgTurnMinutes === null ? '—' : duration(d.avgTurnMinutes, min, hour)}`,
                }))}
                format={v => `${Math.round(v)}`} unit={min} />
            </figure>
            <figure className="chart-card wide">
              <figcaption>{t('kpi.chartHours')}</figcaption>
              <Columns
                bars={r.hours.map(h => ({
                  key: String(h.hour),
                  label: String(h.hour).padStart(2, '0'),
                  value: h.utilization,
                  tip: `${String(h.hour).padStart(2, '0')}:00 — ${pct(h.utilization)} (${t('kpi.slotUseNote', { b: h.booked, c: h.capacity })})`,
                }))}
                max={1} format={v => `${Math.round(v * 100)}%`} />
            </figure>
          </div>

          {r.yard && <YardSection yard={r.yard} />}

          <button type="button" className="link-btn" onClick={() => setTable(x => !x)}>{table ? t('kpi.hideTable') : t('kpi.showTable')}</button>
          {table && (
            <div className="table-wrap kpi-table">
              <table className="grid compact">
                <thead>
                  <tr>
                    <th>{t('common.day')}</th><th>{t('kpi.slotUse')}</th><th>{t('kpi.appointments')}</th><th>{t('status.Completed')}</th>
                    <th>{t('status.NoShow')}</th><th>{t('status.Cancelled')}</th><th>{t('kpi.walkInsShort')}</th><th>{t('kpi.onTime')}</th><th>{t('kpi.turn')}</th>
                  </tr>
                </thead>
                <tbody>
                  {r.days.map(d => (
                    <tr key={d.date}>
                      <td className="nowrap">{dayLabel(d.date, lang)}</td>
                      <td className="num">{pct(d.utilization)}</td>
                      <td className="num">{d.appointments}</td>
                      <td className="num">{d.completed}</td>
                      <td className="num">{d.noShows}</td>
                      <td className="num">{d.cancelled}</td>
                      <td className="num">{d.walkIns}</td>
                      <td className="num">{d.arrived ? `${d.onTime}/${d.arrived}` : '—'}</td>
                      <td className="num">{d.avgTurnMinutes === null ? '—' : duration(d.avgTurnMinutes, min, hour)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </section>
  );
}

/** Inside the terminal: gate to block, at the block, to the exit; by day and by block (slowest first). */
function YardSection({ yard }: { yard: KpiYard }) {
  const { t, lang } = useApp();
  const min = t('unit.min');
  const hour = t('unit.h');
  const time = (m: number | null) => (m === null ? '—' : duration(m, min, hour));
  return (
    <section className="kpi-yard">
      <h2 className="section-title">{t('kpi.yardTitle')}</h2>
      <dl className="tiles">
        <Tile label={t('kpi.toBlock')} value={time(yard.avgToBlockMinutes)} note={t('kpi.toBlockNote')} />
        <Tile label={t('kpi.atBlock')} value={time(yard.avgAtBlockMinutes)} note={t('kpi.atBlockNote')} />
        <Tile label={t('kpi.toExit')} value={time(yard.avgToExitMinutes)} note={t('kpi.toExitNote')} />
        <Tile label={t('kpi.appSeen')} value={pct(yard.appSeenShare)} note={t('kpi.appSeenNote', { n: yard.visits })} />
      </dl>
      <div className="charts">
        <figure className="chart-card">
          <figcaption>{t('kpi.chartAtBlock')}</figcaption>
          <Line
            points={yard.days.map(d => ({
              key: d.date,
              label: shortDay(d.date, lang),
              value: d.avgAtBlockMinutes,
              tip: `${dayLabel(d.date, lang)}: ${time(d.avgAtBlockMinutes)}`,
            }))}
            format={v => `${Math.round(v)}`} unit={min} />
        </figure>
        <figure className="chart-card">
          <figcaption>{t('kpi.blocksTitle')}</figcaption>
          {yard.blocks.length === 0 ? <p className="muted">—</p> : (
            <div className="table-wrap flat">
              <table className="grid compact">
                <thead>
                  <tr><th>{t('kpi.block')}</th><th>{t('kpi.stops')}</th><th>{t('kpi.drive')}</th><th>{t('kpi.atBlock')}</th></tr>
                </thead>
                <tbody>
                  {yard.blocks.slice(0, 12).map(b => (
                    <tr key={b.block}>
                      <td><bdi>{b.block}</bdi></td>
                      <td className="num">{b.stops}</td>
                      <td className="num">{time(b.avgDriveMinutes)}</td>
                      <td className="num">{time(b.avgAtBlockMinutes)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </figure>
      </div>
    </section>
  );
}

function Tile({ label, value, note }: { label: string; value: string; note: string }) {
  return (
    <div className="tile">
      <dt>{label}</dt>
      <dd className="tile-value num">{value}</dd>
      <dd className="tile-note">{note}</dd>
    </div>
  );
}

interface Point {
  key: string;
  label: string;
  value: number | null;
  tip: string;
}

const H = 200;

/** The chart's real width in CSS pixels, so text in the SVG stays at its set size. */
function useWidth(): [RefObject<HTMLDivElement | null>, number] {
  const ref = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(600);
  useEffect(() => {
    if (!ref.current) return;
    const observer = new ResizeObserver(entries => setWidth(Math.max(240, Math.round(entries[0].contentRect.width))));
    observer.observe(ref.current);
    return () => observer.disconnect();
  }, []);
  return [ref, width];
}

/** A round top for the axis: 10, 20, 50, 100… */
function niceTop(v: number): number {
  const p = Math.pow(10, Math.floor(Math.log10(Math.max(v, 1))));
  return [1, 2, 2.5, 5, 10].map(m => m * p).find(n => n >= v) ?? 10 * p;
}
const PAD = { top: 12, right: 8, bottom: 26, left: 40 };

/** Thin columns from one baseline with a hover tooltip; at most ~12 x-labels are shown. */
function Columns({ bars, max, format }: { bars: Point[]; max: number; format: (v: number) => string }) {
  const [hover, setHover] = useState<number | null>(null);
  const [ref, W] = useWidth();
  const top = Math.max(max, ...bars.map(b => b.value ?? 0));
  const plotW = W - PAD.left - PAD.right;
  const plotH = H - PAD.top - PAD.bottom;
  const band = plotW / Math.max(1, bars.length);
  const barW = Math.min(24, band * 0.6);
  const y = (v: number) => PAD.top + plotH - (v / top) * plotH;
  const every = Math.ceil(bars.length / Math.max(2, Math.floor(W / 56)));
  const ticks = [0, top / 2, top];

  if (bars.length === 0) return <p className="muted chart-empty">—</p>;
  return (
    <div className="chart" dir="ltr" ref={ref} onMouseLeave={() => setHover(null)}>
      <svg viewBox={`0 0 ${W} ${H}`} width={W} height={H} role="img" aria-label={bars.map(b => b.tip).join('; ')}>
        {ticks.map(v => (
          <g key={v}>
            <line x1={PAD.left} x2={W - PAD.right} y1={y(v)} y2={y(v)} className="grid-line" />
            <text x={PAD.left - 6} y={y(v) + 4} className="axis-text" textAnchor="end">{format(v)}</text>
          </g>
        ))}
        {bars.map((b, i) => {
          const x = PAD.left + band * i + (band - barW) / 2;
          const v = b.value ?? 0;
          const h = Math.max(0, y(0) - y(v));
          const r = Math.min(4, h / 2, barW / 2);
          return (
            <g key={b.key} onMouseEnter={() => setHover(i)}>
              <rect x={PAD.left + band * i} y={PAD.top} width={band} height={plotH} fill="transparent" />
              {b.value !== null && h > 0 && (
                <path className={hover === i ? 'bar hot' : 'bar'}
                  d={`M${x},${y(0)} V${y(v) + r} Q${x},${y(v)} ${x + r},${y(v)} H${x + barW - r} Q${x + barW},${y(v)} ${x + barW},${y(v) + r} V${y(0)} Z`} />
              )}
              {i % every === 0 && <text x={x + barW / 2} y={H - 8} className="axis-text" textAnchor="middle">{b.label}</text>}
            </g>
          );
        })}
      </svg>
      {hover !== null && <div className="chart-tip" style={{ insetInlineStart: `${((PAD.left + band * (hover + 0.5)) / W) * 100}%` }}>{bars[hover].tip}</div>}
    </div>
  );
}

/** One 2px line with end dots; gaps where a day has no value. */
function Line({ points, format, unit }: { points: Point[]; format: (v: number) => string; unit: string }) {
  const [hover, setHover] = useState<number | null>(null);
  const [ref, W] = useWidth();
  const values = points.map(p => p.value).filter((v): v is number => v !== null);
  if (values.length === 0) return <p className="muted chart-empty">—</p>;
  const top = niceTop(Math.max(10, ...values));
  const plotW = W - PAD.left - PAD.right;
  const plotH = H - PAD.top - PAD.bottom;
  const step = plotW / Math.max(1, points.length);
  const x = (i: number) => PAD.left + step * (i + 0.5);
  const y = (v: number) => PAD.top + plotH - (v / top) * plotH;
  const every = Math.ceil(points.length / Math.max(2, Math.floor(W / 56)));
  const segments: string[] = [];
  let current = '';
  points.forEach((p, i) => {
    if (p.value === null) { if (current) segments.push(current); current = ''; return; }
    current += `${current ? 'L' : 'M'}${x(i)},${y(p.value)} `;
  });
  if (current) segments.push(current);

  return (
    <div className="chart" dir="ltr" ref={ref} onMouseLeave={() => setHover(null)}>
      <svg viewBox={`0 0 ${W} ${H}`} width={W} height={H} role="img" aria-label={points.map(p => p.tip).join('; ')}>
        {[0, top / 2, top].map(v => (
          <g key={v}>
            <line x1={PAD.left} x2={W - PAD.right} y1={y(v)} y2={y(v)} className="grid-line" />
            <text x={PAD.left - 6} y={y(v) + 4} className="axis-text" textAnchor="end">{format(v)}</text>
          </g>
        ))}
        <text x={PAD.left} y={PAD.top - 2} className="axis-text">{unit}</text>
        {segments.map((d, i) => <path key={i} d={d} className="line" />)}
        {hover !== null && <line x1={x(hover)} x2={x(hover)} y1={PAD.top} y2={PAD.top + plotH} className="crosshair" />}
        {points.map((p, i) => (
          <g key={p.key} onMouseEnter={() => setHover(i)}>
            <rect x={PAD.left + step * i} y={PAD.top} width={step} height={plotH} fill="transparent" />
            {p.value !== null && <circle cx={x(i)} cy={y(p.value)} r={hover === i ? 5 : 4} className="dot" />}
            {i % every === 0 && <text x={x(i)} y={H - 8} className="axis-text" textAnchor="middle">{p.label}</text>}
          </g>
        ))}
      </svg>
      {hover !== null && <div className="chart-tip" style={{ insetInlineStart: `${(x(hover) / W) * 100}%` }}>{points[hover].tip}</div>}
    </div>
  );
}
