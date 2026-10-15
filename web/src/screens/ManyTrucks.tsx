import { useMemo, useState, type FormEvent } from 'react';
import { ApiError } from '../lib/api.ts';
import { allocate, groupTrucks, splitContainers, type Visit } from '../lib/plan.ts';
import { isCompanyRole } from '../lib/routes.ts';
import { localDate, slotRange } from '../lib/time.ts';
import type { AdminResponse, Appointment, ContainerLookupResult, LookupContainer, SlotAvailability } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { Notice, TrafficLight } from '../ui/kit.tsx';

type Source = 'bl' | 'list';

interface Outcome {
  visit: Visit;
  appointment?: Appointment;
  error?: string;
}

const selectable = (c: LookupContainer) => c.status === 'Ready' || c.status === 'Warning';

/** Book by bill of lading or by a list of containers: one appointment per truck, spread over the chosen hours. */
export function ManyTrucks({ category, hours, companyId, onBooked }: {
  category: string;
  hours: SlotAvailability[];
  companyId: number | null;
  onBooked: () => void;
}) {
  const { api, t, user, terminal, go } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  const [source, setSource] = useState<Source>('bl');
  const [blNo, setBlNo] = useState('');
  const [text, setText] = useState('');
  const [bookingRef, setBookingRef] = useState('');
  const [finding, setFinding] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [found, setFound] = useState<ContainerLookupResult | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [pair, setPair] = useState(true);
  const [progress, setProgress] = useState<number | null>(null);
  const [outcomes, setOutcomes] = useState<Outcome[] | null>(null);

  const typed = useMemo(() => splitContainers(text), [text]);
  const chosen = (found?.containers ?? []).filter(c => selected.includes(c.containerNo));
  const trucks = groupTrucks(chosen, pair, found?.blNo === null);
  const openHours = hours.filter(h => !h.isClosed && h.available > 0);
  const plan = allocate(trucks, openHours);
  const hasTwenties = chosen.filter(c => c.sizeFt === 20 || c.sizeFt === null).length >= 2;

  async function find(e: FormEvent) {
    e.preventDefault();
    setFinding(true);
    setError(null);
    setFound(null);
    setOutcomes(null);
    try {
      const response = await api.post<AdminResponse<ContainerLookupResult>>(`${base}/containers/lookup`, source === 'bl'
        ? { category, blNo: blNo.trim() }
        : { category, containers: typed, bookingRef: bookingRef.trim() || undefined });
      setFound(response.data);
      setSelected(response.data.containers.filter(selectable).map(c => c.containerNo));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('common.offline'));
    } finally {
      setFinding(false);
    }
  }

  async function book() {
    const results: Outcome[] = [];
    setError(null);
    for (let i = 0; i < plan.visits.length; i++) {
      const visit = plan.visits[i];
      setProgress(i + 1);
      try {
        const appointment = await api.post<Appointment>(`${base}/appointments`, {
          slotId: visit.slot.slotId,
          companyId: isCompanyRole(user.role) ? undefined : companyId,
          items: visit.containers.map(c => ({
            category,
            containerNo: c.containerNo,
            isoType: c.isoType ?? undefined,
            bookingRef: bookingRef.trim() || undefined,
          })),
        });
        results.push({ visit, appointment });
      } catch (err) {
        const message = err instanceof ApiError
          ? (err.checks.filter(c => c.result === 'Fail').map(c => c.message).filter(Boolean).join(' ') || err.message)
          : t('common.offline');
        results.push({ visit, error: message });
      }
      setOutcomes([...results]);
    }
    setProgress(null);
    onBooked();
  }

  if (outcomes && progress === null) {
    const ok = outcomes.filter(o => o.appointment).length;
    const failed = outcomes.filter(o => o.error).flatMap(o => o.visit.containers.map(c => c.containerNo));
    return (
      <div className="book-side">
        <h2>{t('many.done', { ok, n: outcomes.length })}</h2>
        <VisitTable rows={outcomes} />
        <p className="hint">{t('many.assignLater')}</p>
        <div className="actions wrap">
          <button type="button" className="btn btn-primary"
            onClick={() => go('appointments', { date: outcomes[0] ? localDate(new Date(outcomes[0].visit.slot.startAt), terminal.timeZone) : undefined })}>
            {t('many.open')}
          </button>
          {failed.length > 0 && (
            <button type="button" className="btn btn-secondary" onClick={() => { setOutcomes(null); setSelected(failed); }}>
              {t('many.retry', { n: failed.length })}
            </button>
          )}
          <button type="button" className="btn btn-secondary" onClick={() => { setOutcomes(null); setFound(null); setBlNo(''); setText(''); }}>
            {t('many.again')}
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="book-side">
      <form className="block" onSubmit={find}>
        <fieldset className="block">
          <legend>{t('many.bySource')}</legend>
          <div className="segmented" role="radiogroup">
            {(['bl', 'list'] as const).map(s => (
              <label key={s} className={source === s ? 'seg active' : 'seg'}>
                <input type="radio" name="source" checked={source === s} onChange={() => { setSource(s); setFound(null); }} />
                {s === 'bl' ? t('many.bl') : t('many.list')}
              </label>
            ))}
          </div>
        </fieldset>

        {source === 'bl' ? (
          <label className="field"><span>{t('many.blNo')}</span>
            <input value={blNo} dir="ltr" autoComplete="off" onChange={e => setBlNo(e.target.value.toUpperCase())} /></label>
        ) : (
          <>
            <label className="field"><span>{t('many.paste')}</span>
              <textarea value={text} dir="ltr" rows={5} spellCheck={false} onChange={e => setText(e.target.value.toUpperCase())}
                placeholder={'MSCU1234566\nMSCU5523417'} /></label>
            <p className="hint">{typed.length > 0 ? t('many.count', { n: typed.length }) : t('many.pasteHint')}</p>
            <label className="field"><span>{t('many.bookingRef')}</span>
              <input value={bookingRef} dir="ltr" onChange={e => setBookingRef(e.target.value.toUpperCase())} /></label>
          </>
        )}
        <button type="submit" className="btn btn-secondary" disabled={finding || (source === 'bl' ? !blNo.trim() : typed.length === 0)}>
          {finding ? t('many.finding') : t('many.find')}
        </button>
      </form>

      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}

      {found && found.containers.length === 0 && <p className="muted">{t('many.none')}</p>}

      {found && found.containers.length > 0 && (
        <fieldset className="block">
          <legend>{t('many.containers')}</legend>
          <div className="pick-head">
            <span className="muted">{t('many.selected', { n: selected.length, total: found.containers.length })}</span>
            <button type="button" className="link-btn" onClick={() => setSelected(found.containers.filter(selectable).map(c => c.containerNo))}>
              {t('many.selectAll')}
            </button>
          </div>
          <ul className="pick-list">
            {found.containers.map(c => {
              const can = selectable(c);
              const on = selected.includes(c.containerNo);
              return (
                <li key={c.containerNo} className={`pick pick-${c.status.toLowerCase()}`}>
                  <label>
                    <input type="checkbox" disabled={!can} checked={on}
                      onChange={() => setSelected(list => (on ? list.filter(x => x !== c.containerNo) : [...list, c.containerNo]))} />
                    <bdi className="pick-no">{c.containerNo}</bdi>
                    <span className="pick-size num">{c.sizeFt ? `${c.sizeFt}′` : ''}</span>
                  </label>
                  <span className="pick-state" dir="auto">
                    {c.status === 'Ready' ? t('many.ready') : [...c.problems, ...c.warnings].join(' ')}
                  </span>
                </li>
              );
            })}
          </ul>
          {hasTwenties && (
            <>
              <label className="radio">
                <input type="checkbox" checked={pair} onChange={e => setPair(e.target.checked)} />
                {t('many.pair')}
              </label>
              {found.blNo === null && pair && <p className="hint">{t('many.pairUnknown')}</p>}
            </>
          )}
        </fieldset>
      )}

      {found && chosen.length > 0 && (
        <fieldset className="block">
          <legend>{t('many.plan')}</legend>
          {openHours.length === 0 ? (
            <p className="muted">{t('many.pickHours')}</p>
          ) : (
            <>
              <VisitTable rows={plan.visits.map(visit => ({ visit }))} progress={progress} />
              {plan.unplaced.length > 0 && <Notice kind="info">{t('many.unplaced', { n: plan.unplaced.length })}</Notice>}
            </>
          )}
        </fieldset>
      )}

      {found && (
        <button type="button" className="btn btn-primary btn-wide" disabled={plan.visits.length === 0 || progress !== null || !companyId}
          onClick={book}>
          {progress !== null
            ? t('many.progress', { i: progress, n: plan.visits.length })
            : chosen.length === 0 ? t('many.pickContainers')
            : plan.visits.length === 0 ? t('many.pickHours')
            : t('many.submit', { n: plan.visits.length })}
        </button>
      )}
    </div>
  );
}

function VisitTable({ rows, progress }: { rows: Outcome[]; progress?: number | null }) {
  const { t, terminal } = useApp();
  return (
    <div className="table-wrap">
      <table className="grid compact">
        <thead>
          <tr><th>{t('appt.truck')}</th><th>{t('many.hour')}</th><th>{t('many.containers')}</th>{rows.some(r => r.appointment || r.error) && <th>{t('many.result')}</th>}</tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i} className={progress === i + 1 ? 'selected' : undefined}>
              <td className="num">{i + 1}</td>
              <td className="num nowrap">{slotRange(r.visit.slot.startAt, r.visit.slot.endAt, terminal.timeZone)}</td>
              <td>{r.visit.containers.map(c => <bdi key={c.containerNo} className="move-chip">{c.containerNo}</bdi>)}</td>
              {(r.appointment || r.error) && (
                <td>
                  {r.appointment
                    ? <><a className="row-link nowrap" href={`#/appointments?date=${localDate(new Date(r.appointment.slotStart), terminal.timeZone)}&no=${r.appointment.appointmentNo}`}><bdi>{r.appointment.appointmentNo}</bdi></a> <TrafficLight light={r.appointment.trafficLight} /></>
                    : <span className="check-fail" dir="auto">{r.error}</span>}
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
