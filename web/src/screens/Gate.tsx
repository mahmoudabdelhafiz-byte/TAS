import { useState, type FormEvent, type ReactNode } from 'react';
import { ApiError } from '../lib/api.ts';
import { readLookup } from '../lib/gate.ts';
import { duration, stamp } from '../lib/time.ts';
import type { AdminResponse, ArrivalResult, GateBoard, GateBoardTruck, Lane, Trouble } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, Notice, TrafficLight, useLoad } from '../ui/kit.tsx';

export function GateScreen() {
  const { api, t, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/gate`;
  const board = useLoad(() => api.get<GateBoard>(`${base}/board`), [base], 15_000);
  const troubles = useLoad(() => api.get<Trouble[]>(`${base}/troubles`), [base], 15_000);
  const lanes = useLoad(() => api.get<Lane[]>(`${base}/lanes`), [base]);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [exitLane, setExitLane] = useState('');

  const inLanes = (lanes.data ?? []).filter(l => l.isActive && l.direction === 'In');
  const outLanes = (lanes.data ?? []).filter(l => l.isActive && l.direction === 'Out');
  const exit = exitLane || outLanes[0]?.laneCode || '';
  const min = t('unit.min');
  const hour = t('unit.h');

  function refresh(message?: string) {
    if (message) setDone(message);
    board.reload();
    troubles.reload();
  }

  const b = board.data;

  return (
    <section className="screen gate">
      <header className="screen-head">
        <h1>{t('gate.title')}</h1>
        {b && (
          <dl className="figures">
            <div><dt>{t('gate.atGate')}</dt><dd className="num">{b.atGate.length}</dd></div>
            <div><dt>{t('gate.inTerminal')}</dt><dd className="num">{b.inTerminal.length}</dd></div>
            <div><dt>{t('gate.completed')}</dt><dd className="num">{b.completedToday}</dd></div>
            <div><dt>{t('gate.avgTurn')}</dt><dd className="num">{b.averageTurnMinutesToday === null ? '—' : duration(b.averageTurnMinutesToday, min, hour)}</dd></div>
            <div className={b.openTroubles > 0 ? 'figure-alert' : undefined}><dt>{t('gate.openTroubles')}</dt><dd className="num">{b.openTroubles}</dd></div>
          </dl>
        )}
      </header>

      {board.error && <LoadError error={board.error} retry={board.reload} />}
      {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}

      <div className="gate-grid">
        <div className="gate-col">
          <ArrivalForm lanes={inLanes} onResult={() => refresh()} />

          <h2 className="section-title">{t('gate.troubles')}</h2>
          {troubles.error && <LoadError error={troubles.error} retry={troubles.reload} />}
          {troubles.data && troubles.data.length === 0 && <p className="muted">{t('gate.noTroubles')}</p>}
          <ul className="troubles">
            {(troubles.data ?? []).map(x => (
              <TroubleCase key={x.troubleId} trouble={x} onError={setError} onResolved={() => refresh(t('gate.resolved'))} />
            ))}
          </ul>
        </div>

        <div className="gate-col">
          <h2 className="section-title">{t('gate.atGate')}</h2>
          <TruckTable trucks={b?.atGate ?? []} empty={t('gate.noneAtGate')} timeLabel={t('gate.waiting')}
            minutes={x => x.minutesSinceArrival}
            action={x => (
              <ActionButton kind="primary" onError={setError}
                onRun={async () => { await api.post(`${base}/in`, { appointmentNo: x.appointmentNo }); refresh(); }}>
                {t('gate.gateIn')}
              </ActionButton>
            )} />

          <div className="section-row">
            <h2 className="section-title">{t('gate.inTerminal')}</h2>
            {outLanes.length > 1 && (
              <label className="field field-inline">
                <span>{t('gate.lane')}</span>
                <select value={exit} onChange={e => setExitLane(e.target.value)} dir="ltr">
                  {outLanes.map(l => <option key={l.laneId} value={l.laneCode}>{l.laneCode}</option>)}
                </select>
              </label>
            )}
          </div>
          <TruckTable trucks={b?.inTerminal ?? []} empty={t('gate.noneInside')} timeLabel={t('gate.inside')}
            minutes={x => (x.gateInAt ? (Date.now() - new Date(x.gateInAt).getTime()) / 60_000 : x.minutesSinceArrival)}
            action={x => (
              <ActionButton onError={setError} disabled={!exit}
                onRun={async () => { await api.post(`${base}/out`, { laneCode: exit, appointmentNo: x.appointmentNo }); refresh(); }}>
                {t('gate.gateOut')}
              </ActionButton>
            )} />
        </div>
      </div>
    </section>
  );
}

function TruckTable({ trucks, empty, timeLabel, minutes, action }: {
  trucks: GateBoardTruck[];
  empty: string;
  timeLabel: string;
  minutes: (x: GateBoardTruck) => number;
  action: (x: GateBoardTruck) => ReactNode;
}) {
  const { t } = useApp();
  if (trucks.length === 0) return <p className="muted">{empty}</p>;
  return (
    <div className="table-wrap">
      <table className="grid">
        <thead>
          <tr><th>{t('appt.truck')}</th><th>{t('appt.number')}</th><th>{t('common.company')}</th><th>{timeLabel}</th><th /></tr>
        </thead>
        <tbody>
          {trucks.map(x => {
            const m = minutes(x);
            return (
              <tr key={x.appointmentNo}>
                <td className="plate"><bdi>{x.truckPlate ?? '—'}</bdi></td>
                <td className="nowrap"><bdi>{x.appointmentNo}</bdi></td>
                <td>{x.companyName}</td>
                <td className={`num${m >= 60 ? ' late' : ''}`}>{duration(m, t('unit.min'), t('unit.h'))}</td>
                <td className="cell-action">{action(x)}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function ArrivalForm({ lanes, onResult }: { lanes: Lane[]; onResult: () => void }) {
  const { api, t, terminal } = useApp();
  const [lane, setLane] = useState('');
  const [lookup, setLookup] = useState('');
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<ArrivalResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const laneCode = lane || lanes[0]?.laneCode || '';

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      const response = await api.post<AdminResponse<ArrivalResult>>(`/api/terminals/${terminal.terminalId}/gate/arrivals`, {
        laneCode,
        ...readLookup(lookup),
      });
      setResult(response.data);
      setLookup('');
      onResult();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('common.offline'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="arrival" onSubmit={submit}>
      <h2 className="section-title">{t('gate.arrival')}</h2>
      <div className="arrival-row">
        {lanes.length > 1 && (
          <label className="field field-short"><span>{t('gate.lane')}</span>
            <select value={laneCode} onChange={e => setLane(e.target.value)} dir="ltr">
              {lanes.map(l => <option key={l.laneId} value={l.laneCode}>{l.laneCode}</option>)}
            </select></label>
        )}
        <label className="field grow"><span>{t('gate.lookup')}</span>
          <input value={lookup} onChange={e => setLookup(e.target.value)} dir="ltr" autoComplete="off" /></label>
        <button type="submit" className="btn btn-primary" disabled={busy || !lookup.trim() || !laneCode}>{t('gate.check')}</button>
      </div>
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {result && (
        <div className={`decision decision-${result.decision.toLowerCase()}`} role="status">
          <strong>{result.decision === 'Admit' ? t('gate.admit', { no: result.appointmentNo ?? '' }) : t('gate.referred')}</strong>
          {result.light && <TrafficLight light={result.light} />}
          <p dir="auto">{result.message}</p>
          {result.warnings.map((w, i) => <p key={i} className="muted" dir="auto">{w}</p>)}
        </div>
      )}
    </form>
  );
}

function TroubleCase({ trouble, onResolved, onError }: { trouble: Trouble; onResolved: () => void; onError: (m: string) => void }) {
  const { api, t, tx, lang, terminal } = useApp();
  const [note, setNote] = useState('');
  const path = `/api/terminals/${terminal.terminalId}/gate/troubles/${trouble.troubleId}/resolve`;
  const resolve = (action: 'Admit' | 'Reject' | 'Dismiss') => async () => {
    if (action !== 'Dismiss' && !note.trim()) {
      onError(t('gate.noteNeeded'));
      return;
    }
    await api.post(path, { action, note: note.trim() || null, appointmentNo: trouble.appointmentNo });
    onResolved();
  };
  const minutes = (Date.now() - new Date(trouble.createdAt).getTime()) / 60_000;

  return (
    <li className={`trouble trouble-${trouble.reason}`}>
      <div className="trouble-head">
        <strong>{tx('reason', trouble.reason)}</strong>
        <span className="num muted">{trouble.lane ?? ''} {stamp(trouble.createdAt, terminal.timeZone, lang)}{t('common.sep')}{duration(minutes, t('unit.min'), t('unit.h'))}</span>
      </div>
      <div className="trouble-who">
        {trouble.appointmentNo && <bdi>{trouble.appointmentNo}</bdi>}
        {trouble.plateRead && <bdi className="plate">{trouble.plateRead}</bdi>}
      </div>
      <p dir="auto">{trouble.reasonText}</p>
      <label className="field"><span>{t('gate.note')}</span>
        <input value={note} onChange={e => setNote(e.target.value)} /></label>
      <div className="actions wrap">
        {trouble.appointmentNo && <ActionButton kind="primary" onError={onError} onRun={resolve('Admit')}>{t('gate.letIn')}</ActionButton>}
        <ActionButton kind="danger" onError={onError} onRun={resolve('Reject')}>{t('gate.turnAway')}</ActionButton>
        <ActionButton onError={onError} onRun={resolve('Dismiss')}>{t('gate.dismiss')}</ActionButton>
      </div>
    </li>
  );
}
