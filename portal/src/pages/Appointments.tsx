import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { api } from '../api/client';
import type { Appointment, AppointmentItem, AppointmentStatus, TrafficLight } from '../api/types';
import { isCompanyRole, useUser } from '../auth/Auth';
import { LoadError, StatusLight, lightLabelKey } from '../components/ui';
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

const lightOrder: TrafficLight[] = ['Red', 'Amber', 'Green', 'Grey'];

/** What the truck moves: container numbers, or the cargo reference for multipurpose cargo. */
function describeItem(item: AppointmentItem): string {
  return item.containerNo ?? item.cargoRef ?? item.bookingRef ?? item.category;
}

export function countByLight(appointments: Appointment[]): Record<TrafficLight, number> {
  const counts: Record<TrafficLight, number> = { Red: 0, Amber: 0, Green: 0, Grey: 0 };
  for (const a of appointments) counts[a.trafficLight]++;
  return counts;
}

export function AppointmentsPage() {
  const { t, formatDateTime } = useI18n();
  const user = useUser();
  const terminal = useTerminal();
  const [params, setParams] = useSearchParams();

  const today = todayAt(terminal.timeZone);
  const day = /^\d{4}-\d{2}-\d{2}$/.test(params.get('day') ?? '') ? params.get('day')! : today;
  const showCompany = !isCompanyRole(user.role);

  const query = useQuery({
    queryKey: ['appointments', terminal.terminalId, day],
    queryFn: () => api<Appointment[]>(`/api/terminals/${terminal.terminalId}/appointments`, { query: { date: day } }),
    refetchInterval: 60_000, // lights change as holds appear and trucks move
  });

  const setDay = (next: string) => setParams(next === today ? {} : { day: next }, { replace: true });
  const dayLabel = formatDateTime(`${day}T12:00:00Z`, { weekday: 'long', day: 'numeric', month: 'long' }, 'UTC');
  const time = (iso: string) => formatDateTime(iso, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }, terminal.timeZone);

  const appointments = [...(query.data ?? [])].sort((a, b) => a.slotStart.localeCompare(b.slotStart) || a.appointmentNo.localeCompare(b.appointmentNo));
  const counts = countByLight(appointments);

  return (
    <>
      <div className="page-head">
        <div>
          <h1>{t('appointmentsTitle')}</h1>
          <p className="muted">{dayLabel}</p>
        </div>
        <div className="tools">
          <div className="day-picker">
            <button type="button" className="button secondary" aria-label={t('previousDay')} onClick={() => setDay(addDays(day, -1))}>
              <span aria-hidden="true" className="flip">‹</span>
            </button>
            <div className="field">
              <label htmlFor="day" className="visually-hidden">
                {t('day')}
              </label>
              <input id="day" type="date" value={day} onChange={(e) => e.target.value && setDay(e.target.value)} />
            </div>
            <button type="button" className="button secondary" aria-label={t('nextDay')} onClick={() => setDay(addDays(day, 1))}>
              <span aria-hidden="true" className="flip">›</span>
            </button>
          </div>
          {day !== today && (
            <button type="button" className="button quiet" onClick={() => setDay(today)}>
              {t('today')}
            </button>
          )}
        </div>
      </div>

      {query.isError ? (
        <LoadError error={query.error} onRetry={() => void query.refetch()} />
      ) : query.isLoading ? (
        <p className="muted" role="status">
          {t('loading')}
        </p>
      ) : appointments.length === 0 ? (
        <div className="panel empty">
          <p>{t('noAppointments', { day: dayLabel })}</p>
          <p className="muted">{t('noAppointmentsHint')}</p>
        </div>
      ) : (
        <>
          <ul className="counts" aria-label={t('lightCounts')}>
            {lightOrder.map((light) => (
              <li key={light}>
                <strong>{counts[light]}</strong>
                <StatusLight light={light} />
              </li>
            ))}
          </ul>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th scope="col">{t('colTime')}</th>
                  <th scope="col">{t('colAppointment')}</th>
                  {showCompany && <th scope="col">{t('colCompany')}</th>}
                  <th scope="col">{t('colTruck')}</th>
                  <th scope="col">{t('colMoves')}</th>
                  <th scope="col">{t('colStatus')}</th>
                </tr>
              </thead>
              <tbody>
                {appointments.map((a) => (
                  <tr key={a.appointmentNo} data-light={a.trafficLight}>
                    <td>
                      <span className="code">
                        {time(a.slotStart)}–{time(a.slotEnd)}
                      </span>
                    </td>
                    <td>
                      <span className="code">{a.appointmentNo}</span>
                      {a.isDual && <span className="sub">{t('dual')}</span>}
                    </td>
                    {showCompany && <td>{a.companyName}</td>}
                    <td>
                      {a.truckPlate ? <span className="code">{a.truckPlate}</span> : <span className="muted">{t('noTruck')}</span>}
                      {a.driverName && <span className="sub">{a.driverName}</span>}
                    </td>
                    <td>
                      {a.items.map((item, i) => (
                        <span key={i} className={i === 0 ? 'code' : 'code sub'} style={{ display: 'block' }}>
                          {describeItem(item)}
                        </span>
                      ))}
                    </td>
                    <td>
                      <span className="light" data-light={a.trafficLight}>
                        {t(lightLabelKey(a.trafficLight))}
                      </span>
                      <span className="sub">{t(statusKeys[a.status])}</span>
                      {a.trafficReason && (
                        <span className="sub" lang="en">
                          {a.trafficReason}
                        </span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </>
  );
}
