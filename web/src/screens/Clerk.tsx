import { useState } from 'react';
import { ApiError } from '../lib/api.ts';
import { clock, duration } from '../lib/time.ts';
import type { Lane, LaneTransaction, LaneTransactionDetail } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, Notice, useLoad } from '../ui/kit.tsx';
import { LaneStrip, txText } from './Gate.tsx';
import { InspectionEditor } from '../ui/Inspection.tsx';

/** How long after its last read a referred truck can still be at the lane (as the server's recheck window). */
const STILL_AT_LANE_MS = 15 * 60_000;

/** "NO_APPOINTMENT" → "NoAppointment", the trouble reason names the portal translates. */
export const reasonName = (code: string | null) =>
  (code ?? '').toLowerCase().replace(/(^|_)([a-z])/g, (_, __, c: string) => c.toUpperCase());

/**
 * The remote clerk station: every automatic lane at a glance, the trucks waiting for a clerk (oldest first), and for
 * the chosen one its reads and photos, a way to correct what the cameras misread and check again, the intercom, a
 * message to the driver, and let in / turn away.
 */
export function Clerk() {
  const { api, t, tx, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/gate`;
  const lanes = useLoad(() => api.get<Lane[]>(`${base}/lanes`), [base], 5_000);
  const recent = useLoad(() => api.get<LaneTransaction[]>(`${base}/transactions?limit=100`), [base], 3_000);
  const troubles = useLoad(() => api.get<{ troubleId: number }[]>(`${base}/troubles`), [base], 15_000);
  const [chosen, setChosen] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const now = Date.now();
  const waiting = (recent.data ?? [])
    .filter(x => x.status === 'Referred' && now - new Date(x.lastReadAt).getTime() < STILL_AT_LANE_MS)
    .sort((a, b) => a.lastReadAt.localeCompare(b.lastReadAt));
  const linked = new Set(waiting.map(w => w.troubleId));
  const otherCases = (troubles.data ?? []).filter(x => !linked.has(x.troubleId)).length;
  const selected = chosen ?? waiting[0]?.transactionId ?? null;
  const working = (lanes.data ?? []).filter(l => l.isActive);

  function refresh(message?: string) {
    if (message) setDone(message);
    recent.reload();
    lanes.reload();
    troubles.reload();
  }

  return (
    <section className="screen clerk">
      <header className="screen-head">
        <h1>{t('clerk.title')}</h1>
        <dl className="figures">
          <div className={waiting.length > 0 ? 'figure-alert' : undefined}><dt>{t('clerk.waiting')}</dt><dd className="num">{waiting.length}</dd></div>
        </dl>
      </header>
      {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {recent.error && <LoadError error={recent.error} retry={recent.reload} />}
      {working.length > 0 && <LaneStrip lanes={working} onChanged={lanes.reload} onError={setError} />}

      <div className="clerk-grid">
        <aside className="clerk-queue" aria-label={t('clerk.waiting')}>
          <h2 className="section-title">{t('clerk.waiting')}</h2>
          {waiting.length === 0 && <p className="muted">{t('clerk.none')}</p>}
          <ul>
            {waiting.map(w => (
              <li key={w.transactionId}>
                <button type="button" className={`case-pick${w.transactionId === selected ? ' on' : ''}`} onClick={() => setChosen(w.transactionId)}
                  aria-current={w.transactionId === selected}>
                  <span className="case-lane"><bdi>{w.laneCode}</bdi></span>
                  <span className="case-plate"><bdi className="plate">{w.plate ?? w.appointmentNo ?? '—'}</bdi></span>
                  <span className="case-why">{w.reason ? tx('reason', reasonName(w.reason)) : ''}</span>
                  <span className="case-wait num">{duration((now - new Date(w.lastReadAt).getTime()) / 60_000, t('unit.min'), t('unit.h'))}</span>
                </button>
              </li>
            ))}
          </ul>
          {otherCases > 0 && <p className="hint"><a href="#/gate">{t('clerk.otherCases', { n: otherCases })}</a></p>}
        </aside>

        <div className="clerk-case">
          {selected === null
            ? <p className="empty">{t('clerk.pick')}</p>
            : <Case key={selected} id={selected} onDone={refresh} onError={setError} />}
        </div>
      </div>
    </section>
  );
}

function Case({ id, onDone, onError }: { id: number; onDone: (m?: string) => void; onError: (m: string) => void }) {
  const { api, t, tx, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/gate`;
  const detail = useLoad(() => api.get<LaneTransactionDetail>(`${base}/transactions/${id}`), [base, id], 3_000);
  const [fix, setFix] = useState<{ plate: string; containers: string; appointmentNo: string } | null>(null);
  const [note, setNote] = useState('');
  const [message, setMessage] = useState('');
  const [inspecting, setInspecting] = useState(false);

  if (detail.error) return <LoadError error={detail.error} retry={detail.reload} />;
  const d = detail.data;
  if (!d) return null;
  const x = d.transaction;
  const form = fix ?? { plate: x.plate ?? '', containers: x.containers.join(', '), appointmentNo: '' };
  const has = (kind: string) => d.devices.some(dev => dev.isActive && dev.kind === kind);
  const photos = d.reads.flatMap(r => [...r.photos, ...r.damage.map(dm => dm.photo).filter((p): p is string => !!p)]);
  const lanePath = `${base}/lanes/${x.laneId}/commands`;
  const send = (command: string, text?: string) => async () => {
    await api.post(lanePath, { command, text: text ?? null });
  };
  const resolve = (action: 'Admit' | 'Reject') => async () => {
    if (!note.trim()) {
      onError(t('gate.noteNeeded'));
      return;
    }
    await api.post(`${base}/troubles/${x.troubleId}/resolve`, { action, note: note.trim(), appointmentNo: x.appointmentNo });
    onDone(t(action === 'Admit' ? 'clerk.letIn' : 'clerk.turnedAway', { lane: x.laneCode }));
  };
  const stillWaiting = x.status === 'Referred';

  return (
    <article className="case" aria-labelledby="case-title">
      <header className="case-head">
        <h2 id="case-title"><bdi>{x.laneCode}</bdi> · <bdi className="plate">{x.plate ?? '—'}</bdi></h2>
        <span className={`lane-tx lane-tx-${x.status}`}>{t(txText[x.status])}</span>
      </header>
      {x.message && <p className="case-reason" dir="auto"><strong>{x.reason ? tx('reason', reasonName(x.reason)) : ''}</strong> {x.message}</p>}

      {photos.length > 0 && (
        <ul className="photos">
          {photos.map((p, i) => (
            <li key={i}>
              {/^https:\/\//.test(p)
                ? <a href={p} target="_blank" rel="noreferrer"><img src={p} alt={t('clerk.photo', { n: i + 1 })} loading="lazy" /></a>
                : <span className="muted" dir="ltr">{p}</span>}
            </li>
          ))}
        </ul>
      )}

      <div className="table-wrap">
        <table className="grid compact">
          <thead><tr><th>{t('clerk.when')}</th><th>{t('lanes.device')}</th><th>{t('clerk.read')}</th></tr></thead>
          <tbody>
            {d.reads.map(r => (
              <tr key={r.readId}>
                <td className="num">{clock(r.receivedAt, terminal.timeZone)}</td>
                <td><bdi>{r.deviceCode}</bdi></td>
                <td>
                  {r.plate && <bdi className="plate">{r.plate}{r.plateConfidence !== null ? ` (${r.plateConfidence}%)` : ''}</bdi>}{' '}
                  {r.containers.map(c => <bdi key={c.number} className={c.checkDigitOk ? undefined : 'misread'}>{c.number}{c.isoCode ? ` ${c.isoCode}` : ''} </bdi>)}
                  {r.appointmentNo && <bdi>{r.appointmentNo}</bdi>}
                  {r.gatePassScanned && <span>{t('clerk.gatePass')}</span>}
                  {r.tag && <bdi>RFID {r.tag}</bdi>}
                  {r.weightKg !== null && <span className="num"> {r.weightKg} kg</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {stillWaiting && (
        <>
          <form className="case-fix" onSubmit={e => e.preventDefault()}>
            <h3>{t('clerk.fixTitle')}</h3>
            <div className="inline-form">
              <label className="field field-short"><span>{t('clerk.plate')}</span>
                <input dir="ltr" value={form.plate} onChange={e => setFix({ ...form, plate: e.target.value })} /></label>
              <label className="field"><span>{t('clerk.containers')}</span>
                <input dir="ltr" value={form.containers} placeholder="MSCU1234566, …" onChange={e => setFix({ ...form, containers: e.target.value.toUpperCase() })} /></label>
              <label className="field field-short"><span>{t('clerk.appointmentNo')}</span>
                <input dir="ltr" value={form.appointmentNo} onChange={e => setFix({ ...form, appointmentNo: e.target.value.toUpperCase() })} /></label>
              <ActionButton kind="primary" onError={onError} onRun={async () => {
                try {
                  const result = await api.post<{ data: LaneTransaction }>(`${base}/transactions/${id}/recheck`, {
                    plate: form.plate.trim(),
                    containers: form.containers.split(/[\s,;]+/).filter(Boolean),
                    appointmentNo: form.appointmentNo.trim() || null,
                  });
                  setFix(null);
                  onDone(result.data.status === 'Admitted' ? t('clerk.recheckedIn', { lane: x.laneCode }) : t('clerk.recheckedStill'));
                  detail.reload();
                } catch (e) {
                  onError(e instanceof ApiError ? e.message : t('common.offline'));
                }
              }}>{t('clerk.recheck')}</ActionButton>
            </div>
          </form>

          <div className="case-actions">
            <h3>{t('clerk.decide')}</h3>
            <label className="field"><span>{t('gate.note')}</span>
              <input value={note} onChange={e => setNote(e.target.value)} /></label>
            <div className="actions wrap">
              {x.troubleId !== null && x.appointmentNo && <ActionButton kind="primary" onError={onError} onRun={resolve('Admit')}>{t('gate.letIn')}</ActionButton>}
              {x.troubleId !== null && <ActionButton kind="danger" onError={onError} onRun={resolve('Reject')}>{t('gate.turnAway')}</ActionButton>}
            </div>
          </div>
        </>
      )}

      <div className="case-driver">
        <h3>{t('clerk.driver')}</h3>
        <div className="actions wrap">
          {has('Intercom') && <ActionButton onError={onError} onRun={send('Call')}>{t('clerk.call')}</ActionButton>}
          {has('Intercom') && <ActionButton onError={onError} onRun={send('Hangup')}>{t('clerk.hangup')}</ActionButton>}
          {has('Barrier') && <ActionButton onError={onError} onRun={send('Open')}>{t('lanes.openBarrier')}</ActionButton>}
          {x.appointmentNo && <button type="button" className="btn btn-secondary" onClick={() => setInspecting(true)}>{t('insp.inspect')}</button>}
        </div>
        {(has('Display') || has('Kiosk')) && (
          <form className="inline-form" onSubmit={e => e.preventDefault()}>
            <label className="field grow"><span>{t('clerk.message')}</span>
              <input value={message} maxLength={400} onChange={e => setMessage(e.target.value)} /></label>
            <ActionButton disabled={!message.trim()} onError={onError} onRun={async () => {
              await send('Show', message.trim())();
              setMessage('');
            }}>{t('clerk.showMessage')}</ActionButton>
          </form>
        )}
      </div>
      {inspecting && x.appointmentNo && <InspectionEditor appointmentNo={x.appointmentNo} stage="In" onClose={() => setInspecting(false)} />}
    </article>
  );
}
