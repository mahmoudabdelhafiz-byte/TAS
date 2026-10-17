import { useState } from 'react';
import { money } from '../lib/money.ts';
import { addDays, dayLabel, duration } from '../lib/time.ts';
import { useApp } from '../ui/context.tsx';
import { LoadError, Notice, Panel, messageOf, useLoad } from '../ui/kit.tsx';
import { Columns, RangePicker, Tile, pct, shortDay, type DateRange, type KpiDay, type KpiReport } from './Kpi.tsx';

/** One truck, driver or company in a report. */
export interface ReportRow {
  key: string;
  name: string;
  appointments: number;
  completed: number;
  noShows: number;
  cancelled: number;
  noShowRate: number | null;
  onTimeRate: number | null;
  avgTurnMinutes: number | null;
  avgWaitMinutes: number | null;
}

interface CompanyReportData {
  companyId: number;
  companyName: string;
  from: string;
  to: string;
  totals: KpiReport['totals'];
  days: KpiDay[];
  trucks: ReportRow[];
  drivers: ReportRow[];
  holds: { check: string; visits: number; lastReason: string | null }[];
  charges: { currency: string; charges: number; amountMinor: number; noShows: number; lateCancels: number }[];
}

/** Decimals of a currency (SAR 2, KWD 3…), for showing amounts kept in minor units. */
const decimalsOf = (currency: string) => {
  try {
    return new Intl.NumberFormat('en', { style: 'currency', currency }).resolvedOptions().maximumFractionDigits ?? 2;
  } catch {
    return 2;
  }
};

/** Buttons that download reports as Excel files ([files]: label and path under the terminal's /exports). */
export function ExcelButtons({ files }: { files: { label: string; path: string; name: string }[] }) {
  const { api, t, terminal } = useApp();
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const get = async (path: string, name: string) => {
    setBusy(path);
    setError(null);
    try {
      const { blob, fileName } = await api.download(`/api/terminals/${terminal.terminalId}/exports/${path}`);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = fileName ?? name;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e) {
      setError(messageOf(e, t('common.offline')));
    } finally {
      setBusy(null);
    }
  };
  return (
    <div className="excel-buttons">
      {files.map(f => (
        <button key={f.path} type="button" className="btn btn-secondary" disabled={busy !== null} aria-busy={busy === f.path}
          onClick={() => get(f.path, f.name)}>{f.label}</button>
      ))}
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
    </div>
  );
}

/** A trucking company's own performance at the terminal (company users see only their company). */
export function CompanyReportScreen() {
  const { t, today, terminal } = useApp();
  const [range, setRange] = useState<DateRange>({ from: addDays(today, -29), to: today });
  return (
    <section className="screen kpi">
      <header className="screen-head">
        <h1>{t('rep.title', { terminal: terminal.name })}</h1>
        <RangePicker range={range} onChange={setRange} future={false} />
        <ExcelButtons files={[
          { label: t('rep.excel'), path: `company-report?from=${range.from}&to=${range.to}`, name: `company-report-${range.from}-${range.to}.xlsx` },
          { label: t('rep.excelAppointments'), path: `appointments?from=${range.from}&to=${range.to}`, name: `appointments-${range.from}-${range.to}.xlsx` },
        ]} />
      </header>
      <CompanyReportBody range={range} />
    </section>
  );
}

/** The report itself: tiles, appointments by day, trucks, drivers, holds and charges. */
export function CompanyReportBody({ range, companyId }: { range: DateRange; companyId?: number }) {
  const { api, t, tx, lang, terminal } = useApp();
  const query = `from=${range.from}&to=${range.to}${companyId ? `&companyId=${companyId}` : ''}`;
  const report = useLoad(() => api.get<CompanyReportData>(`/api/terminals/${terminal.terminalId}/company-report?${query}`),
    [terminal.terminalId, query]);
  const min = t('unit.min');
  const hour = t('unit.h');
  const r = report.data;
  const time = (m: number | null) => (m === null ? '—' : duration(m, min, hour));

  if (report.error) return <LoadError error={report.error} retry={report.reload} />;
  if (!r) return <p className="muted">{t('common.loading')}</p>;
  if (r.totals.appointments === 0 && r.totals.cancelled === 0) return <p className="empty">{t('rep.none')}</p>;

  return (
    <>
      <dl className="tiles">
        <Tile label={t('kpi.appointments')} value={String(r.totals.appointments)} note={t('kpi.appointmentsNote', { done: r.totals.completed, cancelled: r.totals.cancelled })} />
        <Tile label={t('kpi.noShow')} value={pct(r.totals.noShowRate)} note={t('kpi.noShowNote', { n: r.totals.noShows })} />
        <Tile label={t('kpi.onTime')} value={pct(r.totals.onTimeRate)} note={t('kpi.onTimeNote')} />
        <Tile label={t('kpi.turn')} value={time(r.totals.avgTurnMinutes)} note={t('kpi.turnNote')} />
        <Tile label={t('rep.wait')} value={time(r.totals.avgWaitMinutes)} note={t('rep.waitNote')} />
      </dl>

      <div className="charts">
        <figure className="chart-card">
          <figcaption>{t('rep.chartDaily')}</figcaption>
          <Columns
            bars={r.days.map(d => ({
              key: d.date,
              label: shortDay(d.date, lang),
              value: d.appointments,
              tip: `${dayLabel(d.date, lang)}: ${t('rep.dayTip', { n: d.appointments, noShows: d.noShows })}`,
            }))}
            max={Math.max(1, ...r.days.map(d => d.appointments))} format={v => String(Math.round(v))} />
        </figure>
      </div>

      <div className="rep-grid">
        <section>
          <h2 className="section-title">{t('rep.holds')}</h2>
          {r.holds.length === 0 ? <p className="muted">{t('rep.holdsNone')}</p> : (
            <ul className="rep-holds">
              {r.holds.map(h => (
                <li key={h.check}>
                  <strong>{tx('hold', h.check)}</strong> <span className="num">{t('rep.holdVisits', { n: h.visits })}</span>
                  {h.lastReason && <span className="muted" dir="auto">{h.lastReason}</span>}
                </li>
              ))}
            </ul>
          )}
        </section>
        <section>
          <h2 className="section-title">{t('rep.charges')}</h2>
          {r.charges.length === 0 ? <p className="muted">{t('rep.chargesNone')}</p> : (
            <ul className="rep-holds">
              {r.charges.map(c => (
                <li key={c.currency}>
                  <strong className="num">{money(c.amountMinor, c.currency, decimalsOf(c.currency), lang)}</strong>
                  <span className="muted">{t('rep.chargesNote', { n: c.charges, noShows: c.noShows, late: c.lateCancels })}</span>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>

      <RowTable title={t('rep.trucks')} first={t('rep.truck')} rows={r.trucks} plate />
      <RowTable title={t('rep.drivers')} first={t('rep.driver')} rows={r.drivers} />
    </>
  );
}

/** Rows of trucks, drivers or companies with their figures; [onOpen] makes rows open something. */
export function RowTable({ title, first, rows, plate = false, onOpen }: {
  title: string;
  first: string;
  rows: ReportRow[];
  plate?: boolean;
  onOpen?: (row: ReportRow) => void;
}) {
  const { t } = useApp();
  const min = t('unit.min');
  const hour = t('unit.h');
  if (rows.length === 0) return null;
  return (
    <section>
      <h2 className="section-title">{title}</h2>
      <div className="table-wrap">
        <table className="grid compact">
          <thead>
            <tr>
              <th>{first}</th><th className="num">{t('kpi.appointments')}</th><th className="num">{t('status.Completed')}</th>
              <th className="num">{t('kpi.noShow')}</th><th className="num">{t('rep.noShowRate')}</th><th className="num">{t('kpi.onTime')}</th>
              <th className="num">{t('kpi.turn')}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map(row => (
              <tr key={row.key} className={onOpen ? 'clickable' : undefined} tabIndex={onOpen ? 0 : undefined}
                onClick={onOpen ? () => onOpen(row) : undefined} onKeyDown={onOpen ? e => { if (e.key === 'Enter') onOpen(row); } : undefined}>
                <td className={plate ? 'plate' : undefined}><bdi>{row.name}</bdi></td>
                <td className="num">{row.appointments}</td>
                <td className="num">{row.completed}</td>
                <td className="num">{row.noShows}</td>
                <td className={row.noShowRate !== null && row.noShowRate >= 0.15 ? 'num warn-text' : 'num'}>{pct(row.noShowRate)}</td>
                <td className="num">{pct(row.onTimeRate)}</td>
                <td className="num">{row.avgTurnMinutes === null ? '—' : duration(row.avgTurnMinutes, min, hour)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

/** For the terminal: every company side by side; a company opens its full report. */
export function CompaniesSection({ range }: { range: DateRange }) {
  const { api, t, terminal } = useApp();
  const rows = useLoad(() => api.get<ReportRow[]>(`/api/terminals/${terminal.terminalId}/company-report/companies?from=${range.from}&to=${range.to}`),
    [terminal.terminalId, range.from, range.to]);
  const [open, setOpen] = useState<ReportRow | null>(null);
  return (
    <>
      {rows.error && <LoadError error={rows.error} retry={rows.reload} />}
      {rows.data && rows.data.length > 0 && (
        <>
          <RowTable title={t('rep.companies')} first={t('rep.company')} rows={rows.data} onOpen={setOpen} />
          <p className="hint">{t('rep.companiesNote')}</p>
        </>
      )}
      {open && (
        <Panel title={open.name} onClose={() => setOpen(null)}>
          <ExcelButtons files={[
            { label: t('rep.excel'), path: `company-report?companyId=${open.key}&from=${range.from}&to=${range.to}`, name: `company-report-${range.from}-${range.to}.xlsx` },
            { label: t('rep.excelAppointments'), path: `appointments?companyId=${open.key}&from=${range.from}&to=${range.to}`, name: `appointments-${range.from}-${range.to}.xlsx` },
          ]} />
          <CompanyReportBody range={range} companyId={Number(open.key)} />
        </Panel>
      )}
    </>
  );
}
