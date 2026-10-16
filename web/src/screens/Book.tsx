import { useEffect, useState } from 'react';
import { ApiError } from '../lib/api.ts';
import { isCompanyRole } from '../lib/routes.ts';
import { addDays, dayLabel, localDate, slotRange } from '../lib/time.ts';
import type { Appointment, BookingItemInput, Category, Company, Driver, SlotAvailability, Truck } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { DayStepper, LoadError, Notice, TrafficLight, useLoad } from '../ui/kit.tsx';
import { ManyTrucks } from './ManyTrucks.tsx';
import { money, type FeeSchedule } from '../lib/money.ts';

interface ItemDraft {
  category: string;
  containerNo: string;
  bookingRef: string;
  cargoRef: string;
  tonnage: string;
}

const emptyItem = (category: string): ItemDraft => ({ category, containerNo: '', bookingRef: '', cargoRef: '', tonnage: '' });

export function categoryName(c: Category | undefined, code: string, lang: string): string {
  if (!c) return code;
  return lang === 'ar' ? c.nameAr : c.nameEn;
}

export function Book() {
  const { api, t, lang, user, terminal, today, go, route } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  // From "Track containers": book these containers (import pickups), one truck each or paired.
  const prefill = route.params.containers ?? '';
  const companyUser = isCompanyRole(user.role);

  const [companyId, setCompanyId] = useState<number | null>(user.companyId);
  const [date, setDate] = useState(addDays(today, 1));
  const [items, setItems] = useState<ItemDraft[]>([]);
  const [slotId, setSlotId] = useState<number | null>(null);
  const [truckId, setTruckId] = useState('');
  const [driverId, setDriverId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [refusal, setRefusal] = useState<ApiError | null>(null);
  const [booked, setBooked] = useState<Appointment | null>(null);
  const [many, setMany] = useState(prefill.length > 0);
  const [hourSet, setHourSet] = useState<number[]>([]);

  const categories = useLoad(() => api.get<Category[]>(`${base}/categories`), [base]);
  const companies = useLoad(
    () => (companyUser ? Promise.resolve([] as Company[]) : api.get<Company[]>('/api/companies')),
    [companyUser]);
  const fleet = useLoad(async () => companyId
    ? { trucks: await api.get<Truck[]>(`/api/companies/${companyId}/trucks`), drivers: await api.get<Driver[]>(`/api/companies/${companyId}/drivers`) }
    : { trucks: [] as Truck[], drivers: [] as Driver[] }, [companyId]);

  const mainCategory = items[0]?.category ?? '';
  const slots = useLoad(
    () => (mainCategory
      ? api.get<SlotAvailability[]>(`${base}/availability?date=${date}&category=${mainCategory}${companyId && !companyUser ? `&companyId=${companyId}` : ''}`)
      : Promise.resolve([] as SlotAvailability[])),
    [base, date, mainCategory, companyId]);

  // Start with the first category once the list arrives.
  useEffect(() => {
    if (categories.data?.length && items.length === 0) {
      const first = prefill ? categories.data.find(c => c.code.startsWith('IMP')) ?? categories.data[0] : categories.data[0];
      setItems([emptyItem(first.code)]);
    }
  }, [categories.data, items.length]);

  useEffect(() => { setSlotId(null); setHourSet([]); }, [date, mainCategory]);

  const byCode = new Map((categories.data ?? []).map(c => [c.code, c]));
  const maxDate = addDays(today, Math.max(0, terminal.bookingHorizonDays - 1));
  const now = Date.now();
  const chosen = slots.data?.find(s => s.slotId === slotId) ?? null;
  const containerMove = byCode.get(mainCategory)?.mode === 'Container';
  const severalTrucks = many && containerMove;
  const pickedHours = (slots.data ?? []).filter(s => hourSet.includes(s.slotId));

  function update(index: number, patch: Partial<ItemDraft>) {
    setItems(list => list.map((item, i) => (i === index ? { ...item, ...patch } : item)));
  }

  async function submit() {
    if (!chosen || !companyId) return;
    setBusy(true);
    setError(null);
    setRefusal(null);
    const payload: BookingItemInput[] = items.map(item => {
      const mode = byCode.get(item.category)?.mode;
      return mode === 'Cargo'
        ? { category: item.category, cargoRef: item.cargoRef.trim(), plannedTonnage: item.tonnage ? Number(item.tonnage) : undefined }
        : { category: item.category, containerNo: item.containerNo.trim() || undefined, bookingRef: item.bookingRef.trim() || undefined };
    });
    try {
      const appointment = await api.post<Appointment>(`${base}/appointments`, {
        slotId: chosen.slotId,
        companyId: companyUser ? undefined : companyId,
        truckId: truckId ? Number(truckId) : undefined,
        driverId: driverId ? Number(driverId) : undefined,
        items: payload,
      });
      setBooked(appointment);
      slots.reload();
    } catch (e) {
      if (e instanceof ApiError && e.status === 422) setRefusal(e);
      else setError(e instanceof ApiError ? e.message : t('common.offline'));
    } finally {
      setBusy(false);
    }
  }

  if (booked) {
    return (
      <section className="screen narrow">
        <h1>{t('book.done', { no: booked.appointmentNo })}</h1>
        <div className="booked-card">
          <div className="booked-time">{slotRange(booked.slotStart, booked.slotEnd, terminal.timeZone)}</div>
          <div className="booked-day">{dayLabel(localDate(new Date(booked.slotStart), terminal.timeZone), lang)}</div>
          <TrafficLight light={booked.trafficLight} reason={booked.trafficReason} />
          <p className="muted" dir="auto">{booked.trafficReason}</p>
        </div>
        <p>{t('book.doneLead')}</p>
        <div className="actions">
          <button type="button" className="btn btn-primary"
            onClick={() => go('appointments', { date: localDate(new Date(booked.slotStart), terminal.timeZone), no: booked.appointmentNo })}>
            {t('book.open')}
          </button>
          <button type="button" className="btn btn-secondary"
            onClick={() => { setBooked(null); setItems(categories.data?.length ? [emptyItem(categories.data[0].code)] : []); setSlotId(null); }}>
            {t('book.another')}
          </button>
        </div>
      </section>
    );
  }

  return (
    <section className="screen book">
      <header className="screen-head">
        <h1>{t('book.title')}</h1>
        <DayStepper date={date} onChange={setDate} min={today} max={maxDate} />
      </header>

      {categories.error && <LoadError error={categories.error} retry={categories.reload} />}

      {!companyUser && (
        <label className="field field-inline">
          <span>{t('common.company')}</span>
          <select value={companyId ?? ''} onChange={e => setCompanyId(e.target.value ? Number(e.target.value) : null)}>
            <option value="">{t('common.chooseCompany')}</option>
            {(companies.data ?? []).filter(c => c.status === 'Active').map(c => <option key={c.companyId} value={c.companyId}>{c.name}</option>)}
          </select>
        </label>
      )}

      <div className="book-grid">
        <div className="book-main">
          <fieldset className="block">
            <legend>{t('book.category')}</legend>
            <div className="segmented" role="radiogroup">
              {(categories.data ?? []).map(c => (
                <label key={c.code} className={mainCategory === c.code ? 'seg active' : 'seg'}>
                  <input type="radio" name="category" value={c.code} checked={mainCategory === c.code}
                    onChange={() => setItems(list => [{ ...(list[0] ?? emptyItem(c.code)), category: c.code }, ...list.slice(1)])} />
                  {categoryName(c, c.code, lang)}
                </label>
              ))}
            </div>
          </fieldset>

          {containerMove && (
            <fieldset className="block">
              <legend>{t('book.howMany')}</legend>
              <div className="segmented" role="radiogroup">
                <label className={!many ? 'seg active' : 'seg'}>
                  <input type="radio" name="many" checked={!many} onChange={() => setMany(false)} />{t('book.one')}
                </label>
                <label className={many ? 'seg active' : 'seg'}>
                  <input type="radio" name="many" checked={many} onChange={() => setMany(true)} />{t('book.many')}
                </label>
              </div>
            </fieldset>
          )}

          <fieldset className="block">
            <legend>{severalTrucks ? t('book.hours') : t('book.hour')}</legend>
            {severalTrucks && <p className="hint">{t('book.hoursHint')}</p>}
            {slots.error && <LoadError error={slots.error} retry={slots.reload} />}
            {slots.data && slots.data.length === 0 && !slots.loading && <p className="muted">{t('book.noSlots')}</p>}
            <div className="hours" role={severalTrucks ? 'group' : 'radiogroup'} aria-busy={slots.loading}>
              {(slots.data ?? []).map(s => {
                const past = new Date(s.endAt).getTime() <= now;
                const unavailable = s.isClosed || s.available <= 0 || past;
                return (
                  <label key={s.slotId} className={`hour${(severalTrucks ? hourSet.includes(s.slotId) : slotId === s.slotId) ? ' active' : ''}${unavailable ? ' off' : ''}`}>
                    {severalTrucks
                      ? <input type="checkbox" disabled={unavailable} checked={hourSet.includes(s.slotId)}
                          onChange={() => setHourSet(set => (set.includes(s.slotId) ? set.filter(x => x !== s.slotId) : [...set, s.slotId]))} />
                      : <input type="radio" name="slot" disabled={unavailable} checked={slotId === s.slotId} onChange={() => setSlotId(s.slotId)} />}
                    <span className="hour-time">{slotRange(s.startAt, s.endAt, terminal.timeZone)}</span>
                    <span className="hour-free">
                      {s.isClosed ? t('book.closed') : s.available <= 0 ? t('book.full') : t('book.free', { n: s.available })}
                    </span>
                  </label>
                );
              })}
            </div>
          </fieldset>
        </div>

        {severalTrucks ? (
          <ManyTrucks key={mainCategory} category={mainCategory} hours={pickedHours} companyId={companyId} onBooked={slots.reload}
            initialList={prefill ? prefill.split(',').join('\n') : undefined} />
        ) : (
        <div className="book-side">
          <fieldset className="block">
            <legend>{t('book.moves')}</legend>
            {items.map((item, index) => {
              const category = byCode.get(item.category);
              return (
                <div className="move" key={index}>
                  <div className="move-head">
                    <span className="move-label">{t('book.move', { n: index + 1 })}</span>
                    {index === 0
                      ? <span className="move-cat">{categoryName(category, item.category, lang)}</span>
                      : (
                        <select value={item.category} onChange={e => update(index, { category: e.target.value })} aria-label={t('book.category')}>
                          {(categories.data ?? []).map(c => <option key={c.code} value={c.code}>{categoryName(c, c.code, lang)}</option>)}
                        </select>
                      )}
                    {index > 0 && (
                      <button type="button" className="link-btn" onClick={() => setItems(list => list.filter((_, i) => i !== index))}>
                        {t('book.removeMove')}
                      </button>
                    )}
                  </div>
                  {category?.mode === 'Cargo' ? (
                    <div className="move-fields">
                      <label className="field"><span>{t('book.cargoRef')}</span>
                        <input value={item.cargoRef} dir="ltr" onChange={e => update(index, { cargoRef: e.target.value })} /></label>
                      <label className="field field-short"><span>{t('book.tonnage')}</span>
                        <input inputMode="decimal" value={item.tonnage} dir="ltr" onChange={e => update(index, { tonnage: e.target.value })} /></label>
                    </div>
                  ) : (
                    <div className="move-fields">
                      <label className="field"><span>{t('book.container')}</span>
                        <input value={item.containerNo} dir="ltr" placeholder="MSCU1234566" className="mono-ish"
                          onChange={e => update(index, { containerNo: e.target.value.toUpperCase() })} /></label>
                      <label className="field"><span>{t('book.bookingRef')}</span>
                        <input value={item.bookingRef} dir="ltr" onChange={e => update(index, { bookingRef: e.target.value.toUpperCase() })} /></label>
                    </div>
                  )}
                </div>
              );
            })}
            {items.length > 0 && items.length < 4 && (
              <button type="button" className="btn btn-quiet" onClick={() => setItems(list => [...list, emptyItem(mainCategory)])}>
                {t('book.addMove')}
              </button>
            )}
          </fieldset>

          <fieldset className="block two">
            <label className="field"><span>{t('book.truck')}</span>
              <select value={truckId} onChange={e => setTruckId(e.target.value)} dir="ltr">
                <option value="">{t('book.later')}</option>
                {(fleet.data?.trucks ?? []).filter(x => !x.isBlocked).map(x => <option key={x.truckId} value={x.truckId}>{x.plateNumber}</option>)}
              </select></label>
            <label className="field"><span>{t('book.driver')}</span>
              <select value={driverId} onChange={e => setDriverId(e.target.value)}>
                <option value="">{t('book.later')}</option>
                {(fleet.data?.drivers ?? []).filter(x => !x.isBlocked).map(x => <option key={x.driverId} value={x.driverId}>{x.fullName}</option>)}
              </select></label>
          </fieldset>

          {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
          {refusal && (
            <Notice kind="error" onClose={() => setRefusal(null)}>
              <strong>{t('book.refused')}</strong>
              <ul className="plain">
                {(refusal.checks.length ? refusal.checks.filter(c => c.result !== 'Pass').map(c => c.message ?? c.type) : [refusal.message])
                  .map((m, i) => <li key={i} dir="auto">{m}</li>)}
              </ul>
            </Notice>
          )}

          <FeeNotice />

          <button type="button" className="btn btn-primary btn-wide" disabled={!chosen || !companyId || busy} onClick={submit}>
            {busy ? t('book.busy') : chosen ? t('book.submit', { time: slotRange(chosen.startAt, chosen.endAt, terminal.timeZone) }) : t('book.pickHour')}
          </button>
        </div>
        )}
      </div>
    </section>
  );
}

/** The terminal's fees, shown before booking (nothing when it charges none). */
function FeeNotice() {
  const { api, t, lang, terminal } = useApp();
  const fees = useLoad(() => api.get<FeeSchedule>(`/api/terminals/${terminal.terminalId}/fees`), [terminal.terminalId]).data;
  if (!fees || (fees.booking === 0 && fees.noShow === 0 && fees.lateCancel === 0)) return null;
  const m = (minor: number) => money(minor, fees.currency, fees.decimals, lang);
  return (
    <p className="fee-notice">
      {[
        fees.booking > 0 ? t('fees.bookingLine', { amount: m(fees.booking) }) : null,
        fees.noShow > 0 ? t('fees.noShowLine', { amount: m(fees.noShow) }) : null,
        fees.lateCancel > 0 && fees.lateCancelHours > 0 ? t('fees.lateCancelLine', { amount: m(fees.lateCancel), hours: fees.lateCancelHours }) : null,
      ].filter(Boolean).join(' ')}
    </p>
  );
}
