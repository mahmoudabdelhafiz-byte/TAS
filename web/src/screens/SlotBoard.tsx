import { useState } from 'react';
import { canChangeCapacity } from '../lib/routes.ts';
import { clock, slotRange } from '../lib/time.ts';
import type { AdminResponse, Category, Holiday, Override, OverrideAction, SlotBoardRow } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, DayStepper, LoadError, Notice, Panel, useLoad } from '../ui/kit.tsx';
import { categoryName } from './Book.tsx';
import { SlotAdvicePanel } from './SlotAdvice.tsx';

const actionText = { Close: 'slots.action.Close', Set: 'slots.action.Set', Adjust: 'slots.action.Adjust' } as const;

/** Tones for move types inside a bay, in category sort order. */
const tones = ['var(--tone-1)', 'var(--tone-2)', 'var(--tone-3)', 'var(--tone-4)', 'var(--tone-5)', 'var(--tone-6)'];

export function SlotBoard() {
  const { api, t, lang, user, terminal, today, route, go } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  const date = route.params.date ?? today;
  const [openSlot, setOpenSlot] = useState<number | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const board = useLoad(() => api.get<SlotBoardRow[]>(`${base}/capacity/slot-board?date=${date}`), [base, date], 60_000);
  const categories = useLoad(() => api.get<Category[]>(`${base}/categories`), [base]);
  const rows = board.data ?? [];
  const from = rows[0]?.startAt;
  const to = rows[rows.length - 1]?.endAt;
  const overrides = useLoad(
    () => (from && to
      ? api.get<Override[]>(`${base}/capacity/overrides?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`)
      : Promise.resolve([] as Override[])),
    [base, from, to]);

  // Public holidays of the terminal's country: a reminder to plan the day, never a closure by itself.
  const year = date.slice(0, 4);
  const holidays = useLoad(() => api.get<Holiday[]>(`${base}/holidays?year=${year}`), [base, year]);
  const holiday = (holidays.data ?? []).filter(h => h.date === date);
  const holidayName = [...new Set(holiday.map(h => (lang === 'ar' ? h.nameAr : h.nameEn)))].join(lang === 'ar' ? '، ' : ', ');
  const dayClosed = rows.length > 0 && rows.every(r => r.isClosed);

  const cats = [...(categories.data ?? [])].sort((a, b) => a.sortOrder - b.sortOrder);
  const tone = new Map(cats.map((c, i) => [c.code, tones[i % tones.length]]));
  const byCode = new Map(cats.map(c => [c.code, c]));
  const scale = Math.max(1, ...rows.map(r => Math.max(r.totalCapacity, r.totalBooked)));
  const open = rows.find(r => r.slotId === openSlot) ?? null;
  const editable = canChangeCapacity(user.role);

  const totals = rows.reduce((acc, r) => ({ cap: acc.cap + (r.isClosed ? 0 : r.totalCapacity), booked: acc.booked + r.totalBooked }), { cap: 0, booked: 0 });

  function changed(message: string) {
    setDone(message);
    board.reload();
    overrides.reload();
  }

  return (
    <section className={`screen with-panel${open ? ' panel-open' : ''}`}>
      <div className="screen-main">
        <header className="screen-head">
          <h1>{t('slots.title')}</h1>
          <DayStepper date={date} onChange={d => { setOpenSlot(null); go('slots', { date: d }); }} />
          {rows.length > 0 && <p className="head-figure"><strong className="num">{t('slots.dayTotal', { b: totals.booked, c: totals.cap })}</strong></p>}
        </header>

        {board.error && <LoadError error={board.error} retry={board.reload} />}
        {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
        {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
        {board.data && rows.length === 0 && <p className="empty">{t('slots.empty')}</p>}
        {holiday.length > 0 && (
          <div className="holiday-note" role="note">
            <p>
              <strong>{t('slots.holiday', { name: holidayName })}</strong>{' '}
              {holiday.some(h => h.lunar) ? t('slots.holidayLunar') : t('slots.holidayFixed')}
            </p>
            {editable && rows.length > 0 && !dayClosed && (
              <ActionButton kind="secondary" onError={setError} onRun={async () => {
                await api.post<AdminResponse<Override>>(`${base}/capacity/overrides`, {
                  startAt: rows[0].startAt, endAt: rows[rows.length - 1].endAt, category: null, action: 'Close', value: null, reason: holidayName,
                });
                changed(t('slots.dayClosed'));
              }}>{t('slots.closeDay')}</ActionButton>
            )}
          </div>
        )}

        {rows.length > 0 && (
          <>
            <div className="bays" role="list">
              {rows.map(r => {
                const pct = (n: number) => `${(n / scale) * 100}%`;
                const label = `${slotRange(r.startAt, r.endAt, terminal.timeZone)}: ${r.isClosed ? t('slots.closed') : t('slots.booked', { b: r.totalBooked, c: r.totalCapacity })}`;
                return (
                  <button key={r.slotId} type="button" role="listitem" aria-label={label} title={label}
                    className={`bay${r.isClosed ? ' closed' : ''}${r.overCapacity ? ' over' : ''}${openSlot === r.slotId ? ' active' : ''}`}
                    onClick={() => setOpenSlot(r.slotId === openSlot ? null : r.slotId)}>
                    <span className="bay-count num">{r.isClosed ? '×' : `${r.totalBooked}/${r.totalCapacity}`}</span>
                    <span className="bay-well">
                      <span className="bay-cap" style={{ height: pct(r.totalCapacity) }} />
                      <span className="bay-stack">
                        {r.categories.filter(c => c.booked > 0).map(c => (
                          <span key={c.category} className="bay-part" style={{ height: pct(c.booked), background: tone.get(c.category) }} />
                        ))}
                      </span>
                    </span>
                    <span className="bay-hour num">{clock(r.startAt, terminal.timeZone)}</span>
                  </button>
                );
              })}
            </div>
            <ul className="legend">
              {cats.map(c => (
                <li key={c.code}><span className="legend-swatch" style={{ background: tone.get(c.code) }} />{categoryName(c, c.code, lang)}</li>
              ))}
            </ul>
          </>
        )}

        <h2 className="section-title">{t('slots.overrides')}</h2>
        {overrides.error && <LoadError error={overrides.error} retry={overrides.reload} />}
        {overrides.data && overrides.data.length === 0 && <p className="muted">{t('slots.noOverrides')}</p>}
        {overrides.data && overrides.data.length > 0 && (
          <ul className="overrides">
            {overrides.data.map(o => (
              <li key={o.overrideId}>
                <span className="num">{slotRange(o.startAt, o.endAt, terminal.timeZone)}</span>
                <span>{describe(o, t)}<span className="override-scope">{o.category ? categoryName(byCode.get(o.category), o.category, lang) : t('slots.scopeAll')}</span></span>
                <span className="muted" dir="auto">{o.reason}</span>
                {editable && (
                  <ActionButton kind="secondary" onError={setError}
                    onRun={async () => {
                      await api.post(`${base}/capacity/overrides/${o.overrideId}/deactivate`, { reason: 'Removed on the slot board' });
                      changed(t('slots.removed'));
                    }}>
                    {t('slots.remove')}
                  </ActionButton>
                )}
              </li>
            ))}
          </ul>
        )}

        <SlotAdvicePanel editable={editable} onError={setError} onChanged={changed} />
      </div>

      {open && (
        <Panel title={t('slots.change', { time: slotRange(open.startAt, open.endAt, terminal.timeZone) })} onClose={() => setOpenSlot(null)}>
          <p className="detail-time num">{open.isClosed ? t('slots.closed') : t('slots.booked', { b: open.totalBooked, c: open.totalCapacity })}</p>
          <h3>{t('slots.byCategory')}</h3>
          <ul className="cat-bars">
            {open.categories.map(c => (
              <li key={c.category}>
                <span>{categoryName(byCode.get(c.category), c.category, lang)}</span>
                <span className="cat-bar"><span style={{ width: `${c.quota ? Math.min(100, (c.booked / c.quota) * 100) : 0}%`, background: tone.get(c.category) }} /></span>
                <span className="num">{c.booked}/{c.quota}</span>
              </li>
            ))}
          </ul>
          {editable && (
            <OverrideForm slot={open} categories={cats} onError={setError}
              onSaved={() => changed(t('slots.applied'))} />
          )}
        </Panel>
      )}
    </section>
  );
}

function describe(o: Override, t: ReturnType<typeof useApp>['t']): string {
  if (o.action === 'Close') return t('slots.did.Close');
  if (o.action === 'Set') return t('slots.did.Set', { n: o.value ?? 0 });
  const n = o.value ?? 0;
  return t('slots.did.Adjust', { n: `\u2066${n > 0 ? '+' : ''}${n}\u2069` });
}

function OverrideForm({ slot, categories, onSaved, onError }: {
  slot: SlotBoardRow;
  categories: Category[];
  onSaved: () => void;
  onError: (m: string) => void;
}) {
  const { api, t, lang, terminal } = useApp();
  const [action, setAction] = useState<OverrideAction>('Close');
  const [category, setCategory] = useState('');
  const [value, setValue] = useState('');
  const [reason, setReason] = useState('');
  const needsValue = action !== 'Close';
  const ready = reason.trim() && (!needsValue || (value.trim() !== '' && !Number.isNaN(Number(value))));

  return (
    <form className="subform" onSubmit={e => e.preventDefault()}>
      <fieldset className="block">
        <legend>{t('slots.action')}</legend>
        {(['Close', 'Set', 'Adjust'] as const).map(a => (
          <label key={a} className="radio">
            <input type="radio" name="action" checked={action === a} onChange={() => setAction(a)} />
            {t(actionText[a])}
          </label>
        ))}
      </fieldset>
      <div className="two">
        <label className="field"><span>{t('slots.scope')}</span>
          <select value={category} onChange={e => setCategory(e.target.value)}>
            <option value="">{t('slots.scopeAll')}</option>
            {categories.map(c => <option key={c.code} value={c.code}>{categoryName(c, c.code, lang)}</option>)}
          </select></label>
        {needsValue && (
          <label className="field field-short"><span>{t('slots.value')}</span>
            <input inputMode="numeric" value={value} dir="ltr" onChange={e => setValue(e.target.value)} /></label>
        )}
      </div>
      {action === 'Adjust' && <p className="hint">{t('slots.adjustHint')}</p>}
      {action === 'Close' && <p className="hint">{t('slots.closeWarning')}</p>}
      <label className="field"><span>{t('common.reason')}</span>
        <input value={reason} onChange={e => setReason(e.target.value)} /></label>
      <ActionButton kind="primary" disabled={!ready} onError={onError}
        onRun={async () => {
          await api.post<AdminResponse<Override>>(`/api/terminals/${terminal.terminalId}/capacity/overrides`, {
            startAt: slot.startAt,
            endAt: slot.endAt,
            category: category || null,
            action,
            value: needsValue ? Number(value) : null,
            reason: reason.trim(),
          });
          setReason('');
          setValue('');
          onSaved();
        }}>
        {t('slots.apply')}
      </ActionButton>
    </form>
  );
}
