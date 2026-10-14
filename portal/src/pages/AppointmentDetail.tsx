import { useState, type FormEvent } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useLocation, useParams } from 'react-router';
import { api, ApiError } from '../api/client';
import type { Appointment, AppointmentStatus, Category, Driver, SlotAvailability, Truck } from '../api/types';
import { CheckList } from '../booking/CheckList';
import { categoryName } from '../booking/labels';
import { SlotBoard } from '../booking/SlotBoard';
import { Field, LoadError, Notice, StatusLight } from '../components/ui';
import { useI18n } from '../i18n/I18n';
import type { MessageKey } from '../i18n/en';
import { addDays, todayAt, useTerminal } from '../terminals/Terminals';

const statusKeys: Record<AppointmentStatus, MessageKey> = {
  Draft: 'statusDraft',
  Confirmed: 'statusConfirmed',
  EnRoute: 'statusEnRoute',
  Arrived: 'statusArrived',
  GateIn: 'statusGateIn',
  Completed: 'statusCompleted',
  Cancelled: 'statusCancelled',
  NoShow: 'statusNoShow',
  RejectedAtGate: 'statusRejectedAtGate',
};

/** Like the API: before the gate, an appointment still holds capacity and can be changed. */
export const canChange = (status: AppointmentStatus) => status === 'Confirmed' || status === 'EnRoute' || status === 'Arrived';

type Flash = { kind: 'success' | 'error'; text: string } | null;

export function AppointmentDetailPage() {
  const { no = '' } = useParams();
  const { t, language, formatDateTime } = useI18n();
  const terminal = useTerminal();
  const location = useLocation();
  const queries = useQueryClient();
  const base = `/api/terminals/${terminal.terminalId}`;
  const [flash, setFlash] = useState<Flash>(() => {
    const notice = (location.state as { notice?: string } | null)?.notice;
    return notice ? { kind: 'success', text: notice } : null;
  });

  const appointment = useQuery({
    queryKey: ['appointment', terminal.terminalId, no],
    queryFn: () => api<Appointment>(`${base}/appointments/${encodeURIComponent(no)}`),
    refetchInterval: 60_000,
  });
  const categories = useQuery({ queryKey: ['categories', terminal.terminalId], queryFn: () => api<Category[]>(`${base}/categories`) });

  const time = (iso: string) => formatDateTime(iso, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }, terminal.timeZone);
  const day = (iso: string) => formatDateTime(iso, { weekday: 'long', day: 'numeric', month: 'long' }, terminal.timeZone);

  /** After a change: show the new state everywhere. */
  const apply = (updated: Appointment, text: string) => {
    queries.setQueryData(['appointment', terminal.terminalId, no], updated);
    void queries.invalidateQueries({ queryKey: ['appointments'] });
    setFlash({ kind: 'success', text });
  };
  const fail = (err: unknown) => setFlash({ kind: 'error', text: (err as Error).message });

  if (appointment.isError) {
    return appointment.error instanceof ApiError && appointment.error.status === 404 ? (
      <>
        <Notice kind="error">{t('appointmentNotFound', { no })}</Notice>
        <Link to="/appointments">{t('allAppointments')}</Link>
      </>
    ) : (
      <LoadError error={appointment.error} onRetry={() => void appointment.refetch()} />
    );
  }
  if (!appointment.data) {
    return (
      <p className="muted" role="status">
        {t('loading')}
      </p>
    );
  }

  const a = appointment.data;
  const changeable = canChange(a.status);

  return (
    <>
      <div className="page-head">
        <div>
          <h1>
            {t('appointmentTitle', { no: '' })}
            <span className="code">{a.appointmentNo}</span>
          </h1>
          <p className="muted">
            {day(a.slotStart)}, <span className="code">{time(a.slotStart)}–{time(a.slotEnd)}</span>
          </p>
        </div>
        <Link to={`/appointments?day=${todayAt(terminal.timeZone, new Date(a.slotStart))}`}>{t('allAppointments')}</Link>
      </div>

      {flash && <Notice kind={flash.kind}>{flash.text}</Notice>}

      <div className="detail-grid">
        <div style={{ display: 'grid', gap: '1rem' }}>
          <section className="panel" aria-labelledby="state-title">
            <div className="panel-body">
              <h2 id="state-title" className="visually-hidden">
                {t('colStatus')}
              </h2>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.5rem 1.5rem', alignItems: 'baseline' }}>
                <span style={{ fontSize: 'var(--step-2)' }}>
                  <StatusLight light={a.trafficLight} />
                </span>
                <span>{t(statusKeys[a.status])}</span>
              </div>
              {a.trafficReason && <p lang="en">{a.trafficReason}</p>}
              <dl className="facts">
                <dt className="muted">{t('colCompany')}</dt>
                <dd>{a.companyName}</dd>
                <dt className="muted">{t('truck')}</dt>
                <dd>{a.truckPlate ? <span className="code">{a.truckPlate}</span> : <span className="muted">{t('noTruck')}</span>}</dd>
                <dt className="muted">{t('driver')}</dt>
                <dd>{a.driverName ?? <span className="muted">{t('noTruck')}</span>}</dd>
              </dl>
            </div>
          </section>

          <section className="panel" aria-labelledby="checks-title">
            <div className="panel-body">
              <h2 id="checks-title">{t('checksTitle')}</h2>
              {a.items.map((item, i) => (
                <div key={i} style={{ display: 'grid', gap: '0.5rem' }}>
                  <h3>
                    {categoryName(categories.data, item.category, language)}:{' '}
                    <span className="code">{item.containerNo ?? item.cargoRef ?? item.bookingRef}</span>
                  </h3>
                  {item.checks.length > 0 ? <CheckList checks={item.checks} /> : <p className="muted">{t('checkPending')}</p>}
                </div>
              ))}
              {changeable && (
                <div>
                  <RecheckButton no={a.appointmentNo} base={base} onDone={(x) => apply(x, t('checkedAgain'))} onError={fail} />
                </div>
              )}
            </div>
          </section>
        </div>

        <div style={{ display: 'grid', gap: '1rem' }}>
          {changeable ? (
            <>
              <AssignPanel appointment={a} base={base} onDone={(x) => apply(x, t('assignmentSaved'))} onError={fail} />
              <MovePanel appointment={a} base={base} onDone={(x) => apply(x, t('movedNotice', { time: `${day(x.slotStart)} ${time(x.slotStart)}` }))} onError={fail} />
              <CancelPanel appointment={a} base={base} onDone={(x) => apply(x, t('cancelledNotice'))} onError={fail} />
            </>
          ) : (
            <p className="muted">{t('noChangesNow', { status: t(statusKeys[a.status]) })}</p>
          )}
        </div>
      </div>
    </>
  );
}

interface ActionProps {
  appointment: Appointment;
  base: string;
  onDone: (updated: Appointment) => void;
  onError: (err: unknown) => void;
}

function RecheckButton({ no, base, onDone, onError }: { no: string; base: string; onDone: (a: Appointment) => void; onError: (e: unknown) => void }) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  return (
    <button
      type="button"
      className="button secondary"
      disabled={busy}
      onClick={async () => {
        setBusy(true);
        try {
          onDone(await api<Appointment>(`${base}/appointments/${encodeURIComponent(no)}/revalidate`, { method: 'POST' }));
        } catch (err) {
          onError(err);
        } finally {
          setBusy(false);
        }
      }}
    >
      {t('checkAgain')}
    </button>
  );
}

function AssignPanel({ appointment: a, base, onDone, onError }: ActionProps) {
  const { t } = useI18n();
  const [truckId, setTruckId] = useState<number | ''>(a.truckId ?? '');
  const [driverId, setDriverId] = useState<number | ''>(a.driverId ?? '');
  const [busy, setBusy] = useState(false);
  const trucks = useQuery({ queryKey: ['trucks', a.companyId], queryFn: () => api<Truck[]>(`/api/companies/${a.companyId}/trucks`) });
  const drivers = useQuery({ queryKey: ['drivers', a.companyId], queryFn: () => api<Driver[]>(`/api/companies/${a.companyId}/drivers`) });
  const unchanged = (truckId || null) === a.truckId && (driverId || null) === a.driverId;

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    try {
      onDone(await api<Appointment>(`${base}/appointments/${encodeURIComponent(a.appointmentNo)}/assignment`, {
        method: 'PUT',
        body: { truckId: truckId || null, driverId: driverId || null },
      }));
    } catch (err) {
      onError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="panel" aria-labelledby="assign-title">
      <form className="panel-body" onSubmit={submit}>
        <h2 id="assign-title">{t('stepTruck')}</h2>
        <div className="field">
          <label htmlFor="assign-truck">{t('truck')}</label>
          <select id="assign-truck" value={truckId} onChange={(e) => setTruckId(e.target.value ? Number(e.target.value) : '')}>
            <option value="">{t('assignLater')}</option>
            {(trucks.data ?? []).filter((x) => !x.isBlocked || x.truckId === a.truckId).map((x) => (
              <option key={x.truckId} value={x.truckId}>
                {x.plateNumber}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor="assign-driver">{t('driver')}</label>
          <select id="assign-driver" value={driverId} onChange={(e) => setDriverId(e.target.value ? Number(e.target.value) : '')}>
            <option value="">{t('assignLater')}</option>
            {(drivers.data ?? []).filter((x) => !x.isBlocked || x.driverId === a.driverId).map((x) => (
              <option key={x.driverId} value={x.driverId}>
                {x.fullName}
              </option>
            ))}
          </select>
        </div>
        <div>
          <button type="submit" className="button secondary" disabled={busy || unchanged}>
            {t('saveAssignment')}
          </button>
        </div>
      </form>
    </section>
  );
}

function MovePanel({ appointment: a, base, onDone, onError }: ActionProps) {
  const { t, formatDateTime } = useI18n();
  const terminal = useTerminal();
  const today = todayAt(terminal.timeZone);
  const lastDay = addDays(today, terminal.bookingHorizonDays);
  const [open, setOpen] = useState(false);
  const [day, setDay] = useState(() => todayAt(terminal.timeZone, new Date(a.slotStart)));
  const [slotId, setSlotId] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const category = a.items[0]?.category ?? '';

  const availability = useQuery({
    queryKey: ['availability', terminal.terminalId, day, category, a.companyId],
    queryFn: () => api<SlotAvailability[]>(`${base}/availability`, { query: { date: day, category, companyId: a.companyId } }),
    enabled: open,
  });
  const chosen = availability.data?.find((s) => s.slotId === slotId);
  const time = (iso: string) => formatDateTime(iso, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }, terminal.timeZone);

  async function move() {
    if (slotId === null) return;
    setBusy(true);
    try {
      onDone(await api<Appointment>(`${base}/appointments/${encodeURIComponent(a.appointmentNo)}/reschedule`, { body: { slotId } }));
      setOpen(false);
      setSlotId(null);
    } catch (err) {
      onError(err);
      void availability.refetch();
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="panel" aria-labelledby="move-title">
      <div className="panel-body">
        <h2 id="move-title">{t('moveVisit')}</h2>
        {!open ? (
          <div>
            <button type="button" className="button secondary" onClick={() => setOpen(true)}>
              {t('moveVisit')}
            </button>
          </div>
        ) : (
          <>
            <div className="field">
              <label htmlFor="move-day">{t('day')}</label>
              <input id="move-day" type="date" min={today} max={lastDay} value={day} onChange={(e) => (e.target.value && setDay(e.target.value), setSlotId(null))} />
            </div>
            {availability.data ? (
              availability.data.length === 0 ? (
                <p className="muted">{t('noSlots')}</p>
              ) : (
                <SlotBoard slots={availability.data} selected={slotId} onSelect={setSlotId} timeZone={terminal.timeZone} label={t('moveVisit')} current={a.slotId} />
              )
            ) : (
              <p className="muted" role="status">
                {t('loading')}
              </p>
            )}
            <div className="form-actions">
              <button type="button" className="button" disabled={!chosen || busy} onClick={() => void move()}>
                {chosen ? t('moveHere', { time: time(chosen.startAt) }) : t('pickSlot')}
              </button>
              <button type="button" className="button secondary" onClick={() => (setOpen(false), setSlotId(null))}>
                {t('cancel')}
              </button>
            </div>
          </>
        )}
      </div>
    </section>
  );
}

function CancelPanel({ appointment: a, base, onDone, onError }: ActionProps) {
  const { t } = useI18n();
  const [confirming, setConfirming] = useState(false);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    try {
      onDone(await api<Appointment>(`${base}/appointments/${encodeURIComponent(a.appointmentNo)}/cancel`, { body: { reason: reason.trim() || null } }));
    } catch (err) {
      onError(err);
      setBusy(false);
    }
  }

  return (
    <section className="panel" aria-labelledby="cancel-title">
      <form className="panel-body" onSubmit={submit}>
        <h2 id="cancel-title">{t('cancelVisit')}</h2>
        {!confirming ? (
          <div>
            <button type="button" className="button quiet danger" style={{ paddingInline: 0 }} onClick={() => setConfirming(true)}>
              {t('cancelVisit')}
            </button>
          </div>
        ) : (
          <>
            <Field label={t('cancelReason')} value={reason} onChange={(e) => setReason(e.target.value)} autoFocus />
            <div className="form-actions">
              <button type="submit" className="button" disabled={busy} style={{ background: 'var(--hold)', borderColor: 'var(--hold)' }}>
                {t('confirmCancel')}
              </button>
              <button type="button" className="button secondary" onClick={() => setConfirming(false)}>
                {t('keepVisit')}
              </button>
            </div>
          </>
        )}
      </form>
    </section>
  );
}
