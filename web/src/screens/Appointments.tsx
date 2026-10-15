import { useEffect, useState } from 'react';
import { isCompanyRole } from '../lib/routes.ts';
import { addDays, clock, localDate, slotRange, stamp } from '../lib/time.ts';
import type { Appointment, AppointmentItem, Category, Company, Driver, NotificationView, SlotAvailability, Truck } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, DayStepper, LoadError, Notice, Panel, TrafficLight, useLoad } from '../ui/kit.tsx';
import { categoryName } from './Book.tsx';

const active = (a: Appointment) => a.status === 'Confirmed' || a.status === 'EnRoute';

export function itemLabel(item: AppointmentItem): string {
  return item.containerNo ?? item.cargoRef ?? item.bookingRef ?? item.category;
}

export function Appointments() {
  const { api, t, tx, lang, user, terminal, today, route, go } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  const date = route.params.date ?? today;
  const selected = route.params.no ?? null;
  const companyUser = isCompanyRole(user.role);
  const [companyId, setCompanyId] = useState('');

  const list = useLoad(
    () => api.get<Appointment[]>(`${base}/appointments?date=${date}${companyId ? `&companyId=${companyId}` : ''}`),
    [base, date, companyId], 60_000);
  const companies = useLoad(
    () => (companyUser ? Promise.resolve([] as Company[]) : api.get<Company[]>('/api/companies')), [companyUser]);
  const categories = useLoad(() => api.get<Category[]>(`${base}/categories`), [base]);
  const byCode = new Map((categories.data ?? []).map(c => [c.code, c]));

  const rows = [...(list.data ?? [])].sort((a, b) => a.slotStart.localeCompare(b.slotStart) || a.appointmentNo.localeCompare(b.appointmentNo));

  return (
    <section className={`screen with-panel${selected ? ' panel-open' : ''}`}>
      <div className="screen-main">
        <header className="screen-head">
          <h1>{t('appt.title')}</h1>
          <DayStepper date={date} onChange={d => go('appointments', { date: d })} />
          {!companyUser && (
            <label className="field field-inline">
              <span className="visually-hidden">{t('common.company')}</span>
              <select value={companyId} onChange={e => setCompanyId(e.target.value)}>
                <option value="">{t('common.allCompanies')}</option>
                {(companies.data ?? []).map(c => <option key={c.companyId} value={c.companyId}>{c.name}</option>)}
              </select>
            </label>
          )}
        </header>

        {list.error && <LoadError error={list.error} retry={list.reload} />}

        {list.data && rows.length === 0 && <p className="empty">{t('appt.empty')}</p>}

        {rows.length > 0 && (
          <div className="table-wrap">
            <table className="grid">
              <thead>
                <tr>
                  <th>{t('appt.time')}</th>
                  <th>{t('appt.number')}</th>
                  {!companyUser && <th>{t('common.company')}</th>}
                  <th>{t('appt.truck')}</th>
                  <th>{t('appt.driver')}</th>
                  <th>{t('appt.moves')}</th>
                  <th>{t('appt.status')}</th>
                  <th>{t('appt.light')}</th>
                </tr>
              </thead>
              <tbody>
                {rows.map(a => (
                  <tr key={a.appointmentNo} className={a.appointmentNo === selected ? 'selected' : undefined}
                    onClick={() => go('appointments', { date, no: a.appointmentNo })}>
                    <td className="num">{clock(a.slotStart, terminal.timeZone)}</td>
                    <td className="nowrap"><a className="row-link" href={`#/appointments?date=${date}&no=${a.appointmentNo}`} dir="ltr">{a.appointmentNo}</a></td>
                    {!companyUser && <td>{a.companyName}</td>}
                    <td className="plate">{a.truckPlate ? <bdi>{a.truckPlate}</bdi> : <span className="muted">{t('appt.unassigned')}</span>}</td>
                    <td>{a.driverName ?? <span className="muted">{t('appt.unassigned')}</span>}</td>
                    <td>
                      {a.items.map((item, i) => (
                        <span key={i} className="move-chip" title={categoryName(byCode.get(item.category), item.category, lang)} dir="ltr">
                          {itemLabel(item)}
                        </span>
                      ))}
                    </td>
                    <td><span className={`status status-${a.status}`}>{tx('status', a.status)}</span></td>
                    <td>{active(a) || a.status === 'Arrived' ? <TrafficLight light={a.trafficLight} reason={a.trafficReason} /> : <span className="muted">—</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {selected && (
        <AppointmentPanel key={selected} number={selected} byCode={byCode}
          onClose={() => go('appointments', { date })} onChanged={list.reload} />
      )}
    </section>
  );
}

function AppointmentPanel({ number, byCode, onClose, onChanged }: {
  number: string;
  byCode: Map<string, Category>;
  onClose: () => void;
  onChanged: () => void;
}) {
  const { api, t, tx, lang, terminal } = useApp();
  const path = `/api/terminals/${terminal.terminalId}/appointments/${encodeURIComponent(number)}`;
  const detail = useLoad(() => api.get<Appointment>(path), [path]);
  const messages = useLoad(() => api.get<NotificationView[]>(`${path}/notifications`), [path]);
  const [done, setDone] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [mode, setMode] = useState<'none' | 'move' | 'cancel' | 'assign'>('none');

  const a = detail.data;

  async function run(action: () => Promise<Appointment>, message: (a: Appointment) => string) {
    setError(null);
    setDone(null);
    const updated = await action();
    setDone(message(updated));
    setMode('none');
    detail.reload();
    messages.reload();
    onChanged();
  }

  return (
    <Panel title={<span dir="ltr">{number}</span>} onClose={onClose}>
      {detail.error && <LoadError error={detail.error} retry={detail.reload} />}
      {!a && !detail.error && <p className="muted">{t('common.loading')}</p>}
      {a && (
        <>
          <div className="detail-top">
            <div>
              <div className="detail-time">{slotRange(a.slotStart, a.slotEnd, terminal.timeZone)}</div>
              <div className="muted">{a.companyName}</div>
            </div>
            <span className={`status status-${a.status}`}>{tx('status', a.status)}</span>
          </div>

          {(active(a) || a.status === 'Arrived') && (
            <div className={`light-banner light-banner-${a.trafficLight.toLowerCase()}`}>
              <TrafficLight light={a.trafficLight} />
              <p dir="auto">{a.trafficReason}</p>
            </div>
          )}

          {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
          {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}

          {active(a) && mode === 'none' && (
            <div className="actions wrap">
              <ActionButton kind="primary" onError={setError}
                onRun={() => run(() => api.post<Appointment>(`${path}/revalidate`), u => t('appt.rechecked', { reason: u.trafficReason }))}>
                {t('appt.recheck')}
              </ActionButton>
              {a.status === 'Confirmed' && (
                <ActionButton onError={setError}
                  onRun={() => run(() => api.post<Appointment>(`${path}/start-trip`), () => t('appt.started'))}>
                  {t('appt.startTrip')}
                </ActionButton>
              )}
              <button type="button" className="btn btn-secondary" onClick={() => setMode('move')}>{t('appt.move')}</button>
              <button type="button" className="btn btn-secondary" onClick={() => setMode('assign')}>{t('appt.assign')}</button>
              <button type="button" className="btn btn-danger-quiet" onClick={() => setMode('cancel')}>{t('appt.cancel')}</button>
            </div>
          )}

          {mode === 'move' && (
            <MovePicker appointment={a} onCancel={() => setMode('none')} onError={setError}
              onPick={slot => run(() => api.post<Appointment>(`${path}/reschedule`, { slotId: slot.slotId }),
                u => t('appt.moved', { time: slotRange(u.slotStart, u.slotEnd, terminal.timeZone) }))} />
          )}

          {mode === 'assign' && (
            <AssignForm appointment={a} onCancel={() => setMode('none')} onError={setError}
              onSave={(truckId, driverId) => run(() => api.put<Appointment>(`${path}/assignment`, { truckId, driverId }), () => t('appt.assigned'))} />
          )}

          {mode === 'cancel' && <CancelForm onCancel={() => setMode('none')} onError={setError}
            onConfirm={reason => run(() => api.post<Appointment>(`${path}/cancel`, { reason }), () => t('appt.cancelled'))} />}

          <dl className="facts">
            <dt>{t('appt.truck')}</dt><dd className="plate">{a.truckPlate ? <bdi>{a.truckPlate}</bdi> : t('appt.unassigned')}</dd>
            <dt>{t('appt.driver')}</dt><dd>{a.driverName ?? t('appt.unassigned')}</dd>
          </dl>

          <h3>{t('appt.moves')}</h3>
          <ul className="items">
            {a.items.map((item, i) => (
              <li key={i}>
                <div className="item-head">
                  <span dir="ltr" className="item-ref">{itemLabel(item)}</span>
                  <span className="muted">{categoryName(byCode.get(item.category), item.category, lang)}</span>
                </div>
                <ul className="checks">
                  {item.checks.map((c, j) => (
                    <li key={j} className={`check check-${c.result.toLowerCase()}`}>
                      <span className="check-mark" aria-hidden="true" />
                      <span dir="auto">{c.message ?? c.type}</span>
                    </li>
                  ))}
                </ul>
              </li>
            ))}
          </ul>

          <h3>{t('appt.messages')}</h3>
          {messages.data && messages.data.length === 0 && <p className="muted">{t('appt.noMessages')}</p>}
          <ul className="messages">
            {(messages.data ?? []).map((m, i) => (
              <li key={i}>
                <span className="num">{stamp(m.sentAt ?? m.createdAt, terminal.timeZone, lang)}</span>
                <span>{tx('msg', m.template)}</span>
                <span className="muted">{tx('channel', m.channel)}{t('common.sep')}{tx('msgstatus', m.status)}</span>
              </li>
            ))}
          </ul>
        </>
      )}
    </Panel>
  );
}

function MovePicker({ appointment, onPick, onCancel, onError }: {
  appointment: Appointment;
  onPick: (slot: SlotAvailability) => Promise<void>;
  onCancel: () => void;
  onError: (m: string) => void;
}) {
  const { api, t, terminal, today } = useApp();
  const [date, setDate] = useState(localDate(new Date(appointment.slotStart), terminal.timeZone));
  const category = appointment.items[0]?.category ?? '';
  const slots = useLoad(() => api.get<SlotAvailability[]>(
    `/api/terminals/${terminal.terminalId}/availability?date=${date}&category=${category}&companyId=${appointment.companyId}`),
  [date, category]);
  const now = Date.now();

  return (
    <div className="subform">
      <DayStepper date={date} onChange={setDate} min={today} max={addDays(today, terminal.bookingHorizonDays - 1)} />
      {slots.error && <LoadError error={slots.error} retry={slots.reload} />}
      <div className="hours compact">
        {(slots.data ?? []).filter(s => s.slotId !== appointment.slotId).map(s => {
          const off = s.isClosed || s.available <= 0 || new Date(s.endAt).getTime() <= now;
          return (
            <ActionButton key={s.slotId} disabled={off} onError={onError} onRun={() => onPick(s)}>
              <span className="hour-time">{slotRange(s.startAt, s.endAt, terminal.timeZone)}</span>
              <span className="hour-free">{s.isClosed ? t('book.closed') : s.available <= 0 ? t('book.full') : t('book.free', { n: s.available })}</span>
            </ActionButton>
          );
        })}
      </div>
      <button type="button" className="link-btn" onClick={onCancel}>{t('appt.keep')}</button>
    </div>
  );
}

function AssignForm({ appointment, onSave, onCancel, onError }: {
  appointment: Appointment;
  onSave: (truckId: number | null, driverId: number | null) => Promise<void>;
  onCancel: () => void;
  onError: (m: string) => void;
}) {
  const { api, t } = useApp();
  const [truckId, setTruckId] = useState(appointment.truckId ? String(appointment.truckId) : '');
  const [driverId, setDriverId] = useState(appointment.driverId ? String(appointment.driverId) : '');
  const [trucks, setTrucks] = useState<Truck[]>([]);
  const [drivers, setDrivers] = useState<Driver[]>([]);

  useEffect(() => {
    api.get<Truck[]>(`/api/companies/${appointment.companyId}/trucks`).then(setTrucks).catch(() => setTrucks([]));
    api.get<Driver[]>(`/api/companies/${appointment.companyId}/drivers`).then(setDrivers).catch(() => setDrivers([]));
  }, [api, appointment.companyId]);

  return (
    <div className="subform">
      <div className="two">
        <label className="field"><span>{t('book.truck')}</span>
          <select value={truckId} onChange={e => setTruckId(e.target.value)} dir="ltr">
            <option value="">{t('book.later')}</option>
            {trucks.filter(x => !x.isBlocked || String(x.truckId) === truckId).map(x => <option key={x.truckId} value={x.truckId}>{x.plateNumber}</option>)}
          </select></label>
        <label className="field"><span>{t('book.driver')}</span>
          <select value={driverId} onChange={e => setDriverId(e.target.value)}>
            <option value="">{t('book.later')}</option>
            {drivers.filter(x => !x.isBlocked || String(x.driverId) === driverId).map(x => <option key={x.driverId} value={x.driverId}>{x.fullName}</option>)}
          </select></label>
      </div>
      <div className="actions">
        <ActionButton kind="primary" onError={onError} onRun={() => onSave(truckId ? Number(truckId) : null, driverId ? Number(driverId) : null)}>
          {t('slots.apply')}
        </ActionButton>
        <button type="button" className="link-btn" onClick={onCancel}>{t('appt.keep')}</button>
      </div>
    </div>
  );
}

function CancelForm({ onConfirm, onCancel, onError }: {
  onConfirm: (reason: string) => Promise<void>;
  onCancel: () => void;
  onError: (m: string) => void;
}) {
  const { t } = useApp();
  const [reason, setReason] = useState('');
  return (
    <div className="subform">
      <label className="field"><span>{t('appt.cancelReason')}</span>
        <input value={reason} onChange={e => setReason(e.target.value)} autoFocus /></label>
      <div className="actions">
        <ActionButton kind="danger" disabled={!reason.trim()} onError={onError} onRun={() => onConfirm(reason.trim())}>
          {t('appt.cancel')}
        </ActionButton>
        <button type="button" className="link-btn" onClick={onCancel}>{t('appt.keep')}</button>
      </div>
    </div>
  );
}
