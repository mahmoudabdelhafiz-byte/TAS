import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { api, ApiError } from '../api/client';
import type { Appointment, BookingItem, Cargo, Category, Check, Company, Driver, SlotAvailability, Truck } from '../api/types';
import { isCompanyRole, useUser } from '../auth/Auth';
import { CheckList } from '../booking/CheckList';
import { categoryName } from '../booking/labels';
import { SlotBoard } from '../booking/SlotBoard';
import { Field, LoadError, Notice } from '../components/ui';
import { useI18n } from '../i18n/I18n';
import { containerProblem, normalizeContainer } from '../lib/containerNumber';
import { addDays, todayAt, useTerminal } from '../terminals/Terminals';

export const MAX_MOVES = 4;

export interface MoveDraft {
  key: number;
  category: string;
  containerNo: string;
  bookingRef: string;
  cargoRef: string;
}

/** What the server will get for one move, or the reason it isn't ready yet. */
export function moveToItem(move: MoveDraft, category: Category | undefined): { item?: BookingItem; problem?: 'format' | 'checkDigit' | 'missing' } {
  if (!category) return { problem: 'missing' };
  if (category.mode === 'Cargo') {
    return move.cargoRef ? { item: { category: category.code, cargoRef: move.cargoRef } } : { problem: 'missing' };
  }
  const container = normalizeContainer(move.containerNo);
  const bookingRef = move.bookingRef.trim();
  if (!container && !bookingRef) return { problem: 'missing' };
  if (container) {
    const problem = containerProblem(container);
    if (problem) return { problem };
  }
  return { item: { category: category.code, containerNo: container || undefined, bookingRef: bookingRef || undefined } };
}

let nextKey = 1;
const newMove = (category: string): MoveDraft => ({ key: nextKey++, category, containerNo: '', bookingRef: '', cargoRef: '' });

export function BookPage() {
  const { t, language, formatDateTime } = useI18n();
  const user = useUser();
  const terminal = useTerminal();
  const navigate = useNavigate();
  const queries = useQueryClient();
  const [params] = useSearchParams();
  const staff = !isCompanyRole(user.role);
  const base = `/api/terminals/${terminal.terminalId}`;

  const today = todayAt(terminal.timeZone);
  const lastDay = addDays(today, terminal.bookingHorizonDays);
  const [companyId, setCompanyId] = useState<number | null>(user.companyId);
  const [moves, setMoves] = useState<MoveDraft[]>([]);
  const [day, setDay] = useState(() => {
    const asked = params.get('day');
    return asked && asked >= today && asked <= lastDay ? asked : today;
  });
  const [slotId, setSlotId] = useState<number | null>(null);
  const [truckId, setTruckId] = useState<number | ''>('');
  const [driverId, setDriverId] = useState<number | ''>('');
  const [showErrors, setShowErrors] = useState(false);
  const [failure, setFailure] = useState<{ text: string; checks?: Check[] } | null>(null);
  const [busy, setBusy] = useState(false);

  const companies = useQuery({ queryKey: ['companies'], queryFn: () => api<Company[]>('/api/companies'), enabled: staff });
  const categories = useQuery({
    queryKey: ['categories', terminal.terminalId],
    queryFn: () => api<Category[]>(`${base}/categories`),
    select: (list) => list.filter((c) => c.isActive).sort((a, b) => a.sortOrder - b.sortOrder),
  });

  // Start with one move of the first category once the categories are known.
  useEffect(() => {
    const first = categories.data?.[0];
    if (first && moves.length === 0) setMoves([newMove(first.code)]);
  }, [categories.data, moves.length]);

  const categoryOf = (code: string) => categories.data?.find((c) => c.code === code);
  const firstCategory = moves[0]?.category ?? '';
  const needsCargo = moves.some((m) => categoryOf(m.category)?.mode === 'Cargo');

  const availability = useQuery({
    queryKey: ['availability', terminal.terminalId, day, firstCategory, companyId],
    queryFn: () => api<SlotAvailability[]>(`${base}/availability`, { query: { date: day, category: firstCategory, companyId } }),
    enabled: !!firstCategory && companyId !== null,
  });
  const trucks = useQuery({
    queryKey: ['trucks', companyId],
    queryFn: () => api<Truck[]>(`/api/companies/${companyId}/trucks`),
    enabled: companyId !== null,
  });
  const drivers = useQuery({
    queryKey: ['drivers', companyId],
    queryFn: () => api<Driver[]>(`/api/companies/${companyId}/drivers`),
    enabled: companyId !== null,
  });
  const cargo = useQuery({
    queryKey: ['cargo', terminal.terminalId],
    queryFn: () => api<Cargo[]>(`${base}/cargo`),
    enabled: needsCargo,
  });

  // A slot picked for another day or category no longer applies.
  useEffect(() => {
    if (slotId !== null && availability.data && !availability.data.some((s) => s.slotId === slotId && !s.isClosed && s.available > 0)) setSlotId(null);
  }, [availability.data, slotId]);

  const prepared = moves.map((m) => moveToItem(m, categoryOf(m.category)));
  const ready = prepared.every((p) => p.item) && slotId !== null && companyId !== null;
  const slot = availability.data?.find((s) => s.slotId === slotId);
  const companyCargo = useMemo(
    () => (cargo.data ?? []).filter((c) => (c.companyId === null || c.companyId === companyId) && c.status !== 'Closed'),
    [cargo.data, companyId],
  );

  const update = (key: number, change: Partial<MoveDraft>) => setMoves((all) => all.map((m) => (m.key === key ? { ...m, ...change } : m)));

  async function submit(event: FormEvent) {
    event.preventDefault();
    setShowErrors(true);
    setFailure(null);
    if (!ready) return;
    setBusy(true);
    try {
      const appointment = await api<Appointment>(`${base}/appointments`, {
        body: {
          slotId,
          companyId: staff ? companyId : undefined,
          truckId: truckId || null,
          driverId: driverId || null,
          items: prepared.map((p) => p.item),
        },
      });
      await queries.invalidateQueries({ queryKey: ['appointments'] });
      navigate(`/appointments/${appointment.appointmentNo}`, { state: { notice: t('bookedNotice', { no: appointment.appointmentNo }) } });
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setFailure({ text: t('noCapacityNow') });
        setSlotId(null);
        void availability.refetch();
      } else if (err instanceof ApiError && err.status === 422) {
        setFailure({ text: t('notCleared'), checks: err.problem?.checks });
      } else {
        setFailure({ text: (err as Error).message });
      }
      setBusy(false);
    }
  }

  if (categories.isError) return <LoadError error={categories.error} onRetry={() => void categories.refetch()} />;
  if (!categories.data || moves.length === 0) {
    return (
      <p className="muted" role="status">
        {t('loading')}
      </p>
    );
  }

  const time = (iso: string) => formatDateTime(iso, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }, terminal.timeZone);
  const dayLabel = formatDateTime(`${day}T12:00:00Z`, { weekday: 'long', day: 'numeric', month: 'long' }, 'UTC');
  const moveProblem = (i: number) => {
    const problem = prepared[i]?.problem;
    if (!problem || (!showErrors && problem === 'missing')) return null;
    const move = moves[i]!;
    if (problem === 'missing') return categoryOf(move.category)?.mode === 'Cargo' ? null : t('containerOrBooking');
    if (!move.containerNo) return null;
    return problem === 'checkDigit' ? t('containerCheckDigit') : t('containerFormat');
  };

  return (
    <>
      <div className="page-head">
        <h1>{t('bookTitle')}</h1>
        <Link to="/appointments">{t('allAppointments')}</Link>
      </div>

      <form onSubmit={submit} noValidate>
        <ol className="steps">
          {staff && (
            <li>
              <section className="panel">
                <div className="panel-body">
                  <h2>{t('stepCompany')}</h2>
                  <div className="field" style={{ maxInlineSize: '24rem' }}>
                    <label htmlFor="company">{t('company')}</label>
                    <select
                      id="company"
                      value={companyId ?? ''}
                      onChange={(e) => {
                        setCompanyId(e.target.value ? Number(e.target.value) : null);
                        setTruckId('');
                        setDriverId('');
                      }}
                    >
                      <option value="">{t('chooseCompany')}</option>
                      {(companies.data ?? [])
                        .filter((c) => c.status === 'Active')
                        .map((c) => (
                          <option key={c.companyId} value={c.companyId}>
                            {c.name}
                          </option>
                        ))}
                    </select>
                  </div>
                </div>
              </section>
            </li>
          )}

          <li>
            <section className="panel">
              <div className="panel-body">
                <h2>{t('stepMoves')}</h2>
                <p className="muted">{t('movesHint')}</p>
                <div>
                  {moves.map((m, i) => {
                    const category = categoryOf(m.category);
                    const error = moveProblem(i);
                    return (
                      <fieldset key={m.key} className="move" style={{ border: 0, margin: 0, paddingInline: 0 }}>
                        <legend className="visually-hidden">{t('moveN', { n: i + 1 })}</legend>
                        <div className="field">
                          <label htmlFor={`type-${m.key}`}>{moves.length > 1 ? t('moveN', { n: i + 1 }) : t('moveType')}</label>
                          <select id={`type-${m.key}`} value={m.category} onChange={(e) => update(m.key, { category: e.target.value, cargoRef: '' })}>
                            {categories.data.map((c) => (
                              <option key={c.code} value={c.code}>
                                {language === 'ar' ? c.nameAr : c.nameEn}
                              </option>
                            ))}
                          </select>
                        </div>
                        {category?.mode === 'Cargo' ? (
                          <div className="field" style={{ gridColumn: 'span 2' }}>
                            <label htmlFor={`cargo-${m.key}`}>{t('cargoRelease')}</label>
                            <select id={`cargo-${m.key}`} value={m.cargoRef} onChange={(e) => update(m.key, { cargoRef: e.target.value })} aria-invalid={showErrors && !m.cargoRef ? true : undefined}>
                              <option value="" />
                              {companyCargo.map((c) => (
                                <option key={c.cargoAllocationId} value={c.cargoRef}>
                                  {c.cargoRef}
                                  {c.commodity ? ` — ${c.commodity}` : ''}
                                  {c.totalTrips !== null ? ` (${t('tripsLeft', { n: Math.max(0, c.totalTrips - c.bookedTrips) })})` : ''}
                                </option>
                              ))}
                            </select>
                            {cargo.isSuccess && companyCargo.length === 0 && <span className="hint">{t('noCargo')}</span>}
                          </div>
                        ) : (
                          <>
                            <Field
                              label={t('containerNo')}
                              value={m.containerNo}
                              onChange={(e) => update(m.key, { containerNo: e.target.value.toUpperCase() })}
                              onBlur={() => update(m.key, { containerNo: normalizeContainer(m.containerNo) })}
                              placeholder="MSCU1234566"
                              autoComplete="off"
                              spellCheck={false}
                              maxLength={15}
                              dir="ltr"
                              error={error}
                            />
                            <Field
                              label={t('bookingRefOptional')}
                              value={m.bookingRef}
                              onChange={(e) => update(m.key, { bookingRef: e.target.value })}
                              autoComplete="off"
                              dir="ltr"
                            />
                          </>
                        )}
                        {moves.length > 1 && (
                          <button type="button" className="button quiet danger remove" onClick={() => setMoves((all) => all.filter((x) => x.key !== m.key))}>
                            {t('removeMove', { n: i + 1 })}
                          </button>
                        )}
                      </fieldset>
                    );
                  })}
                </div>
                {moves.length < MAX_MOVES && (
                  <div>
                    <button type="button" className="button secondary" onClick={() => setMoves((all) => [...all, newMove(categories.data[0]!.code)])}>
                      {t('addMove')}
                    </button>
                  </div>
                )}
              </div>
            </section>
          </li>

          <li>
            <section className="panel">
              <div className="panel-body">
                <h2>{t('stepSlot')}</h2>
                <div className="day-picker">
                  <button type="button" className="button secondary" aria-label={t('previousDay')} disabled={day <= today} onClick={() => setDay(addDays(day, -1))}>
                    <span aria-hidden="true" className="flip">
                      ‹
                    </span>
                  </button>
                  <div className="field">
                    <label htmlFor="book-day" className="visually-hidden">
                      {t('day')}
                    </label>
                    <input id="book-day" type="date" min={today} max={lastDay} value={day} onChange={(e) => e.target.value && setDay(e.target.value)} />
                  </div>
                  <button type="button" className="button secondary" aria-label={t('nextDay')} disabled={day >= lastDay} onClick={() => setDay(addDays(day, 1))}>
                    <span aria-hidden="true" className="flip">
                      ›
                    </span>
                  </button>
                  <span className="muted" style={{ alignSelf: 'center', marginInlineStart: '0.5rem' }}>
                    {dayLabel}
                  </span>
                </div>
                {companyId === null ? (
                  <p className="muted">{t('chooseCompany')}</p>
                ) : availability.isError ? (
                  <LoadError error={availability.error} onRetry={() => void availability.refetch()} />
                ) : !availability.data ? (
                  <p className="muted" role="status">
                    {t('loading')}
                  </p>
                ) : availability.data.length === 0 ? (
                  <p className="muted">{t('noSlots')}</p>
                ) : (
                  <>
                    <p className="muted">{t('freePlacesFor', { category: categoryName(categories.data, firstCategory, language) })}</p>
                    <SlotBoard
                      slots={availability.data}
                      selected={slotId}
                      onSelect={setSlotId}
                      timeZone={terminal.timeZone}
                      label={t('stepSlot')}
                    />
                    {showErrors && slotId === null && <p style={{ color: 'var(--hold)' }}>{t('pickSlot')}</p>}
                  </>
                )}
              </div>
            </section>
          </li>

          <li>
            <section className="panel">
              <div className="panel-body">
                <h2>{t('stepTruck')}</h2>
                <div className="form-grid" style={{ maxInlineSize: '40rem' }}>
                  <div className="field">
                    <label htmlFor="truck">{t('truck')}</label>
                    <select id="truck" value={truckId} onChange={(e) => setTruckId(e.target.value ? Number(e.target.value) : '')} disabled={companyId === null}>
                      <option value="">{t('assignLater')}</option>
                      {(trucks.data ?? [])
                        .filter((x) => !x.isBlocked)
                        .map((x) => (
                          <option key={x.truckId} value={x.truckId}>
                            {x.plateNumber}
                          </option>
                        ))}
                    </select>
                  </div>
                  <div className="field">
                    <label htmlFor="driver">{t('driver')}</label>
                    <select id="driver" value={driverId} onChange={(e) => setDriverId(e.target.value ? Number(e.target.value) : '')} disabled={companyId === null}>
                      <option value="">{t('assignLater')}</option>
                      {(drivers.data ?? [])
                        .filter((x) => !x.isBlocked)
                        .map((x) => (
                          <option key={x.driverId} value={x.driverId}>
                            {x.fullName}
                          </option>
                        ))}
                    </select>
                  </div>
                </div>
                <p className="muted">{t('assignLaterHint')}</p>
              </div>
            </section>
          </li>
        </ol>

        {failure && (
          <div style={{ marginBlockStart: '1rem' }}>
            <Notice kind="error">
              <p>{failure.text}</p>
              {failure.checks && <CheckList checks={failure.checks} onlyProblems />}
            </Notice>
          </div>
        )}

        <div className="summary-bar" style={{ marginBlockStart: '1rem' }}>
          <span>
            {slot
              ? t('bookingSummary', {
                  moves: moves.length === 1 ? t('oneMove') : t('movesCount', { n: moves.length }),
                  day: dayLabel,
                  time: time(slot.startAt),
                })
              : t('pickSlot')}
          </span>
          <button type="submit" className="button" disabled={busy}>
            {busy ? t('bookingVisit') : t('bookVisit')}
          </button>
        </div>
      </form>
    </>
  );
}
