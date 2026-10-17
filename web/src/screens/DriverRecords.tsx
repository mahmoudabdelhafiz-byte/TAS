import { useState } from 'react';
import { canChangeCapacity, isCompanyRole } from '../lib/routes.ts';
import { stamp } from '../lib/time.ts';
import type { AdminResponse } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, Notice, Panel, useLoad } from '../ui/kit.tsx';

export const incidentKinds = ['Speeding', 'UnsafeDriving', 'NoPpe', 'WrongArea', 'Damage', 'Abusive', 'NoShow', 'Other'] as const;
export type IncidentKind = typeof incidentKinds[number];
type Standing = 'Clear' | 'Warning' | 'OverLimit';
type Dispute = 'Open' | 'Accepted' | 'Rejected';

const defaultPoints: Record<IncidentKind, number> = {
  Speeding: 3, UnsafeDriving: 4, NoPpe: 2, WrongArea: 2, Damage: 5, Abusive: 5, NoShow: 1, Other: 2,
};

interface PointsRow {
  driverId: number;
  fullName: string;
  mobile: string;
  companyName: string;
  points: number;
  standing: Standing;
  open: number;
  lastAt: string | null;
}

interface Incident {
  incidentId: number;
  appointmentNo: string | null;
  kind: IncidentKind;
  points: number;
  occurredAt: string;
  note: string | null;
  recordedByName: string | null;
  disputeStatus: Dispute | null;
  disputeText: string | null;
  disputeAnswer: string | null;
  withdrawnAt: string | null;
  withdrawReason: string | null;
  counts: boolean;
  countsUntil: string;
}

interface DriverRecord {
  driverId: number;
  fullName: string;
  mobile: string;
  companyName: string;
  points: number;
  standing: Standing;
  warnPoints: number;
  limitPoints: number;
  pointsDays: number;
  incidents: Incident[];
}

/** Drivers with penalty points at the terminal, and each driver's record (route param driver=… opens one). */
export function DriverRecords() {
  const { api, t, lang, terminal, user, route, go } = useApp();
  const company = isCompanyRole(user.role);
  const [q, setQ] = useState('');
  const base = `/api/terminals/${terminal.terminalId}/driver-records`;
  const list = useLoad(() => api.get<PointsRow[]>(`${base}${q.trim() ? `?q=${encodeURIComponent(q.trim())}` : ''}`), [base, q]);
  const open = route.params.driver ? Number(route.params.driver) : null;

  return (
    <section className={`screen with-panel${open ? ' panel-open' : ''}`}>
      <div>
        <header className="screen-head">
          <h1>{t('drv.title')}</h1>
          <label className="field field-inline"><span>{t('drv.search')}</span>
            <input value={q} onChange={e => setQ(e.target.value)} autoComplete="off" /></label>
        </header>
        <p className="hint">{company ? t('drv.leadCompany') : t('drv.lead')}</p>
        {list.error && <LoadError error={list.error} retry={list.reload} />}
        {list.data && list.data.length === 0 && <p className="empty">{t('drv.none')}</p>}
        {list.data && list.data.length > 0 && (
          <div className="table-wrap">
            <table className="grid">
              <thead><tr>
                <th>{t('drv.driver')}</th><th>{t('common.company')}</th><th>{t('fleet.mobile')}</th>
                <th className="num">{t('drv.points')}</th><th>{t('drv.standing')}</th><th>{t('drv.disputes')}</th><th>{t('drv.last')}</th>
              </tr></thead>
              <tbody>
                {list.data.map(r => (
                  <tr key={r.driverId} className={open === r.driverId ? 'selected clickable' : 'clickable'} tabIndex={0}
                    onClick={() => go('drivers', { driver: String(r.driverId) })} onKeyDown={e => { if (e.key === 'Enter') go('drivers', { driver: String(r.driverId) }); }}>
                    <td>{r.fullName}</td>
                    <td>{r.companyName}</td>
                    <td className="num"><bdi>{r.mobile}</bdi></td>
                    <td className="num">{r.points}</td>
                    <td><StandingBadge standing={r.standing} /></td>
                    <td>{r.open > 0 ? <span className="warn-text">{t('drv.openN', { n: r.open })}</span> : ''}</td>
                    <td className="num">{r.lastAt ? stamp(r.lastAt, terminal.timeZone, lang) : ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {canChangeCapacity(user.role) && <RecordRules />}
      </div>
      {open && (
        <Panel title={t('drv.recordTitle')} onClose={() => { go('drivers', {}); list.reload(); }}>
          <DriverRecordPanel driverId={open} onChanged={list.reload} />
        </Panel>
      )}
    </section>
  );
}

function StandingBadge({ standing }: { standing: Standing }) {
  const { tx } = useApp();
  return <span className={`standing standing-${standing}`}>{tx('standing', standing)}</span>;
}

/** One driver's record: points against the thresholds, the entries, recording a new one (staff), disputes and withdrawals. */
export function DriverRecordPanel({ driverId, onChanged }: { driverId: number; onChanged?: () => void }) {
  const { api, t, tx, lang, terminal, user } = useApp();
  const staff = !isCompanyRole(user.role);
  const decides = canChangeCapacity(user.role);
  const path = `/api/terminals/${terminal.terminalId}/driver-records/${driverId}`;
  const record = useLoad(() => api.get<DriverRecord>(path), [path]);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const r = record.data;

  const changed = () => { record.reload(); onChanged?.(); };

  if (record.error) return <LoadError error={record.error} retry={record.reload} />;
  if (!r) return <p className="muted">{t('common.loading')}</p>;

  return (
    <div className="driver-record">
      <div className="record-head">
        <div>
          <strong>{r.fullName}</strong> <span className="muted">· {r.companyName} · <bdi>{r.mobile}</bdi></span>
        </div>
        <div className="record-points">
          <span className="num record-points-value">{r.points}</span>
          <span>{t('drv.pointsNow')}</span>
          <StandingBadge standing={r.standing} />
        </div>
        <p className="hint">
          {t('drv.rulesLine', { days: r.pointsDays })}{' '}
          {r.warnPoints > 0 && t('drv.warnAt', { n: r.warnPoints })}{' '}
          {r.limitPoints > 0 && t('drv.limitAt', { n: r.limitPoints })}
        </p>
      </div>
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}

      {staff && <AddIncident path={`${path}/incidents`} onAdded={() => { setDone(t('drv.added')); changed(); }} onError={setError} />}

      <h3 className="section-title">{t('drv.entries')}</h3>
      {r.incidents.length === 0 && <p className="muted">{t('drv.noEntries')}</p>}
      <ol className="incidents">
        {r.incidents.map(i => (
          <li key={i.incidentId} className={i.counts ? 'incident' : 'incident incident-off'}>
            <div className="incident-head">
              <strong>{tx('incident', i.kind)}</strong>
              <span className="num incident-points">{t('drv.pointsN', { n: i.points })}</span>
              <span className="num muted">{stamp(i.occurredAt, terminal.timeZone, lang)}</span>
              {i.appointmentNo && <span className="muted"><bdi>{i.appointmentNo}</bdi></span>}
            </div>
            {i.note && <p dir="auto">{i.note}</p>}
            <p className="muted small">
              {i.recordedByName && <>{t('drv.recordedBy', { name: i.recordedByName })} · </>}
              {i.withdrawnAt ? t('drv.withdrawn', { reason: i.withdrawReason ?? '' })
                : i.counts ? t('drv.countsUntil', { date: stamp(i.countsUntil, terminal.timeZone, lang) }) : t('drv.noLonger')}
            </p>
            {i.disputeStatus && (
              <div className={`dispute dispute-${i.disputeStatus}`}>
                <p><strong>{tx('drvDispute', i.disputeStatus)}</strong> <span dir="auto">{i.disputeText}</span></p>
                {i.disputeAnswer && <p className="muted" dir="auto">{t('drv.answer', { text: i.disputeAnswer })}</p>}
                {decides && i.disputeStatus === 'Open' && (
                  <Decide path={`/api/terminals/${terminal.terminalId}/driver-incidents/${i.incidentId}/decision`} onDone={changed} onError={setError} />
                )}
              </div>
            )}
            {decides && !i.withdrawnAt && (
              <Withdraw path={`/api/terminals/${terminal.terminalId}/driver-incidents/${i.incidentId}/withdraw`} onDone={changed} onError={setError} />
            )}
          </li>
        ))}
      </ol>
    </div>
  );
}

function AddIncident({ path, onAdded, onError }: { path: string; onAdded: () => void; onError: (m: string) => void }) {
  const { api, t, tx } = useApp();
  const [kind, setKind] = useState<IncidentKind>('Speeding');
  const [points, setPoints] = useState(defaultPoints.Speeding);
  const [appointmentNo, setAppointmentNo] = useState('');
  const [note, setNote] = useState('');
  return (
    <form className="add-incident form-stack" onSubmit={e => e.preventDefault()}>
      <h3 className="section-title">{t('drv.addTitle')}</h3>
      <div className="add-incident-row">
        <label className="field"><span>{t('drv.kind')}</span>
          <select value={kind} onChange={e => { const k = e.target.value as IncidentKind; setKind(k); setPoints(defaultPoints[k]); }}>
            {incidentKinds.map(k => <option key={k} value={k}>{tx('incident', k)}</option>)}
          </select></label>
        <label className="field"><span>{t('drv.points')}</span>
          <input type="number" min={1} max={10} value={points} onChange={e => setPoints(Number(e.target.value))} /></label>
        <label className="field"><span>{t('drv.visit')}</span>
          <input value={appointmentNo} dir="ltr" onChange={e => setAppointmentNo(e.target.value)} placeholder="DEMO-…" /></label>
      </div>
      <label className="field"><span>{kind === 'Other' ? t('drv.noteRequired') : t('drv.note')}</span>
        <textarea rows={2} maxLength={500} value={note} dir="auto" onChange={e => setNote(e.target.value)} /></label>
      <ActionButton kind="primary" onError={onError} onRun={async () => {
        await api.post<AdminResponse<DriverRecord>>(path, { kind, points, appointmentNo: appointmentNo.trim() || null, note: note.trim() || null });
        setNote('');
        setAppointmentNo('');
        onAdded();
      }}>{t('drv.add')}</ActionButton>
    </form>
  );
}

function Decide({ path, onDone, onError }: { path: string; onDone: () => void; onError: (m: string) => void }) {
  const { api, t } = useApp();
  const [answer, setAnswer] = useState('');
  return (
    <div className="decide">
      <label className="field"><span>{t('drv.answerLabel')}</span>
        <textarea rows={2} maxLength={500} value={answer} dir="auto" onChange={e => setAnswer(e.target.value)} /></label>
      <div className="decide-buttons">
        <ActionButton kind="primary" disabled={!answer.trim()} onError={onError}
          onRun={async () => { await api.post(path, { accept: true, answer }); onDone(); }}>{t('drv.accept')}</ActionButton>
        <ActionButton kind="secondary" disabled={!answer.trim()} onError={onError}
          onRun={async () => { await api.post(path, { accept: false, answer }); onDone(); }}>{t('drv.reject')}</ActionButton>
      </div>
    </div>
  );
}

function Withdraw({ path, onDone, onError }: { path: string; onDone: () => void; onError: (m: string) => void }) {
  const { api, t } = useApp();
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState('');
  if (!open) return <button type="button" className="link-btn" onClick={() => setOpen(true)}>{t('drv.withdraw')}</button>;
  return (
    <div className="decide">
      <label className="field"><span>{t('drv.withdrawWhy')}</span>
        <input value={reason} maxLength={300} dir="auto" onChange={e => setReason(e.target.value)} /></label>
      <div className="decide-buttons">
        <ActionButton kind="danger" disabled={!reason.trim()} onError={onError}
          onRun={async () => { await api.post(path, { reason }); setOpen(false); onDone(); }}>{t('drv.withdraw')}</ActionButton>
        <button type="button" className="btn btn-quiet" onClick={() => setOpen(false)}>{t('fleet.cancel')}</button>
      </div>
    </div>
  );
}

const ruleKeys = ['driver_points_days', 'driver_warn_points', 'driver_limit_points', 'driver_record_company'] as const;

/** The terminal's rules for the driver record (planners and terminal admins). */
function RecordRules() {
  const { api, t, terminal } = useApp();
  const path = `/api/terminals/${terminal.terminalId}/settings`;
  const settings = useLoad(() => api.get<Record<string, number>>(path), [path]);
  const [values, setValues] = useState<Record<string, number> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const v = values ?? settings.data;
  if (!v) return null;
  const set = (k: string, n: number) => setValues({ ...v, [k]: n });
  return (
    <section className="record-rules form-stack">
      <h2 className="section-title">{t('drv.rulesTitle')}</h2>
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
      <div className="add-incident-row">
        <label className="field"><span>{t('drv.ruleDays')}</span>
          <input type="number" min={1} max={1825} value={v.driver_points_days ?? 180} onChange={e => set('driver_points_days', Number(e.target.value))} /></label>
        <label className="field"><span>{t('drv.ruleWarn')}</span>
          <input type="number" min={0} max={100} value={v.driver_warn_points ?? 6} onChange={e => set('driver_warn_points', Number(e.target.value))} /></label>
        <label className="field"><span>{t('drv.ruleLimit')}</span>
          <input type="number" min={0} max={100} value={v.driver_limit_points ?? 0} onChange={e => set('driver_limit_points', Number(e.target.value))} /></label>
      </div>
      <label className="check"><input type="checkbox" checked={(v.driver_record_company ?? 1) !== 0}
        onChange={e => set('driver_record_company', e.target.checked ? 1 : 0)} /> {t('drv.ruleCompany')}</label>
      <p className="hint">{t('drv.rulesHint')}</p>
      <ActionButton kind="primary" disabled={values === null} onError={setError} onRun={async () => {
        await api.put(path, { values: Object.fromEntries(ruleKeys.map(k => [k, v[k] ?? 0])), reason: null });
        setValues(null);
        settings.reload();
        setDone(t('slots.applied'));
      }}>{t('drv.rulesSave')}</ActionButton>
    </section>
  );
}
