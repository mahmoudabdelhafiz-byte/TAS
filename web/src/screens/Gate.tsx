import { useState, type FormEvent, type ReactNode } from 'react';
import { ApiError } from '../lib/api.ts';
import { readLookup } from '../lib/gate.ts';
import { clock, duration, stamp } from '../lib/time.ts';
import type { AdminResponse, ArrivalResult, GateBoard, GateBoardTruck, Lane, LaneState, Trouble } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, Notice, TrafficLight, useLoad } from '../ui/kit.tsx';
import { GateTicket } from '../ui/GateTicket.tsx';
import { InspectionEditor } from '../ui/Inspection.tsx';

export function GateScreen() {
  const { api, t, terminal } = useApp();
  const autogate = terminal.autogate ?? false;
  const base = `/api/terminals/${terminal.terminalId}/gate`;
  const board = useLoad(() => api.get<GateBoard>(`${base}/board`), [base], 15_000);
  const troubles = useLoad(() => api.get<Trouble[]>(`${base}/troubles`), [base], 15_000);
  const lanes = useLoad(() => api.get<Lane[]>(`${base}/lanes`), [base], 30_000);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [exitLane, setExitLane] = useState('');
  const [ticket, setTicket] = useState<string | null>(null);
  const [lastOut, setLastOut] = useState<string | null>(null);
  const [inspect, setInspect] = useState<{ no: string; stage: 'In' | 'Out' } | null>(null);
  const [held, setHeld] = useState<{ no: string; problems: string[] } | null>(null);
  const [heldNote, setHeldNote] = useState('');

  const working = (lanes.data ?? []).filter(l => l.isActive);
  const inLanes = working.filter(l => l.direction === 'In' && l.state === 'Open');
  const outLanes = working.filter(l => l.direction === 'Out' && l.state === 'Open');
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

      {working.length > 0 && <LaneStrip lanes={working} onChanged={lanes.reload} onError={setError} />}

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
              <span className="row-actions">
                {autogate && <button type="button" className="link-btn" onClick={() => setInspect({ no: x.appointmentNo, stage: 'In' })}>{t('insp.inspect')}</button>}
                <ActionButton kind="primary" onError={setError}
                  onRun={async () => { await api.post(`${base}/in`, { appointmentNo: x.appointmentNo }); refresh(); }}>
                  {t('gate.gateIn')}
                </ActionButton>
              </span>
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
              <span className="row-actions">
                {autogate && <button type="button" className="link-btn" onClick={() => setInspect({ no: x.appointmentNo, stage: 'Out' })}>{t('insp.inspect')}</button>}
                {autogate && <button type="button" className="link-btn" onClick={() => setTicket(x.appointmentNo)}>{t('ticket.open')}</button>}
                <ActionButton onError={setError} disabled={!exit}
                  onRun={async () => {
                    try {
                      await api.post(`${base}/out`, { laneCode: exit, appointmentNo: x.appointmentNo });
                    } catch (e) {
                      if (e instanceof ApiError && e.status === 409 && e.errors.length > 0) {
                        setHeld({ no: x.appointmentNo, problems: e.errors });
                        return;
                      }
                      throw e;
                    }
                    setLastOut(x.appointmentNo);
                    refresh();
                  }}>
                  {t('gate.gateOut')}
                </ActionButton>
              </span>
            )} />
          {held && (
            <div className="exit-held" role="alert">
              <p><strong>{t('exit.held', { no: held.no })}</strong></p>
              <ul>{held.problems.map((p, i) => <li key={i} dir="auto">{p}</li>)}</ul>
              <div className="inline-ask wrap">
                <input value={heldNote} maxLength={300} placeholder={t('exit.why')} aria-label={t('exit.why')} onChange={e => setHeldNote(e.target.value)} />
                <ActionButton kind="danger" disabled={!heldNote.trim()} onError={setError} onRun={async () => {
                  await api.post(`${base}/out`, { laneCode: exit, appointmentNo: held.no, letOut: true, note: heldNote.trim() });
                  setLastOut(held.no);
                  setHeld(null);
                  setHeldNote('');
                  refresh();
                }}>{t('exit.letOut')}</ActionButton>
                <button type="button" className="link-btn" onClick={() => setHeld(null)}>{t('exit.keep')}</button>
              </div>
            </div>
          )}
          {lastOut && autogate && <p className="hint"><button type="button" className="link-btn" onClick={() => setTicket(lastOut)}>{t('ticket.openLast', { no: lastOut })}</button></p>}
        </div>
      </div>
      {ticket && <GateTicket appointmentNo={ticket} onClose={() => setTicket(null)} />}
      {inspect && <InspectionEditor appointmentNo={inspect.no} stage={inspect.stage} onClose={() => setInspect(null)} />}
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

export const txText = {
  Open: 'lanes.tx.Open', Admitted: 'lanes.tx.Admitted', Referred: 'lanes.tx.Referred', Passed: 'lanes.tx.Passed', Abandoned: 'lanes.tx.Abandoned',
} as const;

export const laneStateText = { Open: 'lanes.state.Open', Closed: 'lanes.state.Closed', OutOfOrder: 'lanes.state.OutOfOrder' } as const;

/** Every lane at a glance; the gate staff close a lane (with a reason everyone then sees) and open it again. */
export function LaneStrip({ lanes, onChanged, onError }: { lanes: Lane[]; onChanged: () => void; onError: (m: string) => void }) {
  const { t } = useApp();
  return (
    <ul className="lane-strip" aria-label={t('lanes.strip')}>
      {lanes.map(l => <LaneTile key={l.laneId} lane={l} onChanged={onChanged} onError={onError} />)}
    </ul>
  );
}

function LaneTile({ lane, onChanged, onError }: { lane: Lane; onChanged: () => void; onError: (m: string) => void }) {
  const { api, t, terminal } = useApp();
  const [closing, setClosing] = useState(false);
  const hasBarrier = lane.devices.some(d => d.isActive && d.kind === 'Barrier');
  const trouble = lane.devices.filter(d => d.isActive && (d.connection === 'Offline' || d.connection === 'Fault'));
  const [state, setState] = useState<LaneState>('Closed');
  const [note, setNote] = useState('');
  const url = `/api/terminals/${terminal.terminalId}/gate/lanes/${lane.laneId}/state`;
  const set = (next: LaneState, why: string | null) => async () => {
    await api.post(url, { state: next, note: why });
    setClosing(false);
    setNote('');
    onChanged();
  };
  return (
    <li className={`lane-tile lane-${lane.state}`}>
      <div className="lane-tile-head">
        <strong><bdi>{lane.laneCode}</bdi></strong>
        <span className="lane-dir">{t(lane.direction === 'In' ? 'lanes.in' : 'lanes.out')}{lane.mode === 'Automatic' ? ` · ${t('lanes.mode.Automatic')}` : ''}</span>
      </div>
      <p className="lane-state">{t(laneStateText[lane.state])}</p>
      {lane.stateNote && <p className="lane-note" dir="auto">{lane.stateNote}</p>}
      {trouble.length > 0 && <p className="lane-alert">{t('lanes.devicesDown')} {trouble.map(d => <bdi key={d.deviceId} className="lane-dev">{d.deviceCode}</bdi>)}</p>}
      {lane.transaction && lane.mode === 'Automatic' && (
        <p className={`lane-tx lane-tx-${lane.transaction.status}`} role="status">
          <strong>{t(txText[lane.transaction.status])}</strong>
          {lane.transaction.appointmentNo && <> <bdi>{lane.transaction.appointmentNo}</bdi></>}
          {lane.transaction.status === 'Referred' && lane.transaction.message && <span className="lane-tx-why" dir="auto">{lane.transaction.message}</span>}
        </p>
      )}
      {lane.lastRead && (
        <p className="lane-read" title={lane.lastRead.deviceCode}>
          <span className="muted">{clock(lane.lastRead.receivedAt, terminal.timeZone)}</span>{' '}
          <bdi className="plate">{lane.lastRead.plate ?? lane.lastRead.tag ?? lane.lastRead.appointmentNo ?? ''}</bdi>{' '}
          {lane.lastRead.containers.map(c => <bdi key={c.number} className={c.checkDigitOk ? undefined : 'misread'}>{c.number} </bdi>)}
        </p>
      )}
      {lane.state === 'Open' && hasBarrier && (
        <ActionButton onError={onError} onRun={async () => {
          await api.post(`/api/terminals/${terminal.terminalId}/gate/lanes/${lane.laneId}/commands`, { command: 'Open' });
          onChanged();
        }}>{t('lanes.openBarrier')}</ActionButton>
      )}
      {lane.state === 'Open' && !closing && (
        <button type="button" className="link-btn" onClick={() => setClosing(true)}>{t('lanes.close')}</button>
      )}
      {lane.state === 'Open' && closing && (
        <div className="lane-close">
          <select value={state} onChange={e => setState(e.target.value as LaneState)} aria-label={t('lanes.stateLabel')}>
            <option value="Closed">{t('lanes.state.Closed')}</option>
            <option value="OutOfOrder">{t('lanes.state.OutOfOrder')}</option>
          </select>
          <input value={note} maxLength={200} placeholder={t('lanes.why')} aria-label={t('lanes.why')} autoFocus onChange={e => setNote(e.target.value)} />
          <div className="actions">
            <ActionButton kind="primary" disabled={!note.trim()} onError={onError} onRun={set(state, note.trim())}>{t(state === 'Closed' ? 'lanes.doClose' : 'lanes.doOutOfOrder')}</ActionButton>
            <button type="button" className="link-btn" onClick={() => setClosing(false)}>{t('lanes.keepOpen')}</button>
          </div>
        </div>
      )}
      {lane.state !== 'Open' && <ActionButton onError={onError} onRun={set('Open', null)}>{t('lanes.open')}</ActionButton>}
    </li>
  );
}
