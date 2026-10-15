import { useEffect, useMemo, useRef, useState } from 'react';
import { canChangeCapacity } from '../lib/routes.ts';
import { addDays, stamp, zonedIso } from '../lib/time.ts';
import type { AdminResponse, Category, Company } from '../lib/types.ts';
import {
  cellKey, compress, days, expand, rectangle, slotStarts, toTime, weekTotals, type Cell, type Grid, type PlanWindow,
} from '../lib/weekplan.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, Notice, useLoad } from '../ui/kit.tsx';
import { categoryName } from './Book.tsx';

interface Template {
  templateId: number;
  name: string;
  isDefault: boolean;
  validFrom: string | null;
  validTo: string | null;
  priority: number;
  isActive: boolean;
  windows: PlanWindow[];
}

interface Regenerated {
  created: number;
  updated: number;
  closed: number;
  overCapacity: number;
}

type Tab = 'plans' | 'reserved' | 'rules';

const tabText = { plans: 'setup.plans', reserved: 'setup.reserved', rules: 'setup.rules' } as const;

export function CapacitySetup() {
  const { t, user, route, go } = useApp();
  const tab: Tab = route.params.tab === 'reserved' || route.params.tab === 'rules' ? route.params.tab : 'plans';
  const editable = canChangeCapacity(user.role);

  return (
    <section className="screen">
      <header className="screen-head">
        <h1>{t('setup.title')}</h1>
        <div className="segmented" role="tablist">
          {(['plans', 'reserved', 'rules'] as const).map(x => (
            <button key={x} type="button" role="tab" aria-selected={tab === x} className={tab === x ? 'seg active' : 'seg'}
              onClick={() => go('setup', { tab: x })}>
              {t(tabText[x])}
            </button>
          ))}
        </div>
      </header>
      {tab === 'plans' && <Plans editable={editable} />}
      {tab === 'reserved' && <Reserved editable={editable} />}
      {tab === 'rules' && <Rules editable={editable} />}
    </section>
  );
}

/** "Saved. 120 slots updated…" from the API's regeneratedSlots. */
function useSavedText() {
  const { t } = useApp();
  return (r: unknown) => {
    const g = r as Regenerated | null;
    if (!g) return t('setup.savedNoSlots');
    return t('setup.saved', { n: g.created + g.updated + g.closed }) + (g.overCapacity > 0 ? ` ${t('setup.overWarning', { n: g.overCapacity })}` : '');
  };
}

// ================================================================== weekly plans

function Plans({ editable }: { editable: boolean }) {
  const { api, t, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  const templates = useLoad(() => api.get<Template[]>(`${base}/capacity/templates`), [base]);
  const categories = useLoad(() => api.get<Category[]>(`${base}/categories`), [base]);
  const [selected, setSelected] = useState<number | 'new' | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const list = [...(templates.data ?? [])].sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || (a.validFrom ?? '').localeCompare(b.validFrom ?? ''));
  const current = selected === 'new' ? null : list.find(x => x.templateId === selected) ?? list.find(x => x.isDefault) ?? list[0] ?? null;
  const defaultPlan = list.find(x => x.isDefault) ?? null;

  if (templates.error) return <LoadError error={templates.error} retry={templates.reload} />;
  if (!templates.data || !categories.data) return <p className="muted">{t('common.loading')}</p>;

  return (
    <div className="plans">
      <aside className="plan-list">
        <ul>
          {list.map(p => (
            <li key={p.templateId}>
              <button type="button" className={selected !== 'new' && current?.templateId === p.templateId ? 'plan-item active' : 'plan-item'}
                onClick={() => { setSelected(p.templateId); setDone(null); }}>
                <span className="plan-name">{p.name}</span>
                <span className="plan-when">
                  {p.isDefault ? t('setup.defaultPlan') : <bdi dir="ltr">{`${p.validFrom ?? '…'} – ${p.validTo ?? '…'}`}</bdi>}
                </span>
              </button>
            </li>
          ))}
        </ul>
        {editable && (
          <button type="button" className="btn btn-quiet" onClick={() => { setSelected('new'); setDone(null); }}>{t('setup.newSeasonal')}</button>
        )}
      </aside>
      <div className="plan-main">
        {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
        {(current || selected === 'new') && (
          <PlanEditor key={selected === 'new' ? 'new' : current!.templateId}
            plan={selected === 'new' ? null : current} copyFrom={defaultPlan} categories={categories.data} editable={editable}
            onSaved={(message, id) => { setDone(message); templates.reload(); if (id) setSelected(id); }} />
        )}
      </div>
    </div>
  );
}

function PlanEditor({ plan, copyFrom, categories, editable, onSaved }: {
  plan: Template | null;
  copyFrom: Template | null;
  categories: Category[];
  editable: boolean;
  onSaved: (message: string, id?: number) => void;
}) {
  const { api, t, lang, terminal, today } = useApp();
  const step = terminal.slotMinutes;
  const savedText = useSavedText();
  const [grid, setGrid] = useState<Grid>(() => expand((plan ?? copyFrom)?.windows ?? [], step));
  const [name, setName] = useState(plan?.name ?? '');
  const [validFrom, setValidFrom] = useState(plan?.validFrom ?? today);
  const [validTo, setValidTo] = useState(plan?.validTo ?? addDays(today, 30));
  const [priority, setPriority] = useState(String(plan?.priority ?? 10));
  const [reason, setReason] = useState('');
  const [selection, setSelection] = useState<string[]>([]);
  const [dirty, setDirty] = useState(plan === null);
  const [error, setError] = useState<string | null>(null);
  const anchor = useRef<{ day: number; minute: number } | null>(null);
  const dragging = useRef(false);

  useEffect(() => {
    const stop = () => { dragging.current = false; };
    window.addEventListener('mouseup', stop);
    return () => window.removeEventListener('mouseup', stop);
  }, []);

  const starts = slotStarts(step);
  const isDefault = plan?.isDefault ?? false;
  const max = Math.max(1, ...[...grid.values()].map(c => c.total));
  const totals = weekTotals(grid);
  const dayNames = useMemo(() => days.map((_, i) => new Intl.DateTimeFormat(lang === 'ar' ? 'ar-u-nu-latn' : 'en-GB', { weekday: 'short', timeZone: 'UTC' })
    .format(new Date(Date.UTC(2026, 9, 18 + i)))), [lang]); // 18 Oct 2026 is a Sunday

  function pick(day: number, minute: number, extend: boolean) {
    if (!editable) return;
    if (extend && anchor.current) {
      setSelection(rectangle(anchor.current, { day, minute }, step));
    } else {
      anchor.current = { day, minute };
      setSelection([cellKey(days[day], minute)]);
    }
  }

  function update(keys: string[], cell: Cell | null) {
    setGrid(g => {
      const next = new Map(g);
      for (const k of keys) { if (cell) next.set(k, { ...cell, quotas: { ...cell.quotas } }); else next.delete(k); }
      return next;
    });
    setDirty(true);
  }

  async function save() {
    setError(null);
    const body = {
      name: name.trim(),
      isDefault,
      validFrom: isDefault ? null : validFrom,
      validTo: isDefault ? null : validTo,
      priority: isDefault ? 0 : Number(priority) || 0,
      windows: compress(grid, step),
      reason: reason.trim() || null,
    };
    const base = `/api/terminals/${terminal.terminalId}/capacity/templates`;
    const response = plan
      ? await api.put<AdminResponse<Template>>(`${base}/${plan.templateId}`, body)
      : await api.post<AdminResponse<Template>>(base, body);
    setDirty(false);
    setReason('');
    onSaved(savedText(response.regeneratedSlots), response.data.templateId);
  }

  return (
    <div className="plan-editor">
      <div className="plan-fields">
        <label className="field"><span>{t('setup.planName')}</span>
          <input value={name} disabled={!editable} onChange={e => { setName(e.target.value); setDirty(true); }} /></label>
        {!isDefault && (
          <>
            <label className="field field-short"><span>{t('setup.from')}</span>
              <input type="date" value={validFrom} disabled={!editable} onChange={e => { setValidFrom(e.target.value); setDirty(true); }} /></label>
            <label className="field field-short"><span>{t('setup.to')}</span>
              <input type="date" value={validTo} disabled={!editable} onChange={e => { setValidTo(e.target.value); setDirty(true); }} /></label>
            <label className="field field-tiny"><span>{t('setup.priority')}</span>
              <input inputMode="numeric" value={priority} disabled={!editable} dir="ltr" onChange={e => { setPriority(e.target.value); setDirty(true); }} /></label>
          </>
        )}
        <p className="plan-summary num">{t('setup.weekSummary', { slots: totals.slots, trucks: totals.trucks })}</p>
      </div>
      {isDefault && <p className="hint">{t('setup.defaultHint')}</p>}
      {!plan && <p className="hint">{t('setup.seasonalHint')}</p>}

      <div className="plan-body">
        <div className="week-grid-wrap" onMouseLeave={() => { dragging.current = false; }}>
          <table className="week-grid" aria-label={t('setup.plans')}>
            <thead>
              <tr>
                <th />
                {days.map((d, i) => (
                  <th key={d}>
                    <button type="button" className="link-btn" disabled={!editable}
                      onClick={() => { anchor.current = { day: i, minute: 0 }; setSelection(starts.map(m => cellKey(d, m))); }}>
                      {dayNames[i]}
                    </button>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {starts.map(m => (
                <tr key={m}>
                  <th scope="row">
                    <button type="button" className="link-btn num" disabled={!editable}
                      onClick={() => { anchor.current = { day: 0, minute: m }; setSelection(days.map(d => cellKey(d, m))); }}>
                      {toTime(m)}
                    </button>
                  </th>
                  {days.map((d, i) => {
                    const key = cellKey(d, m);
                    const cell = grid.get(key);
                    const on = selection.includes(key);
                    return (
                      <td key={d} className={`${cell ? '' : 'shut'}${on ? ' picked' : ''}`}
                        style={cell ? { background: `color-mix(in srgb, var(--teal) ${Math.round(12 + (cell.total / max) * 55)}%, white)` } : undefined}
                        onMouseDown={e => { e.preventDefault(); dragging.current = true; pick(i, m, e.shiftKey); }}
                        onMouseEnter={() => { if (dragging.current && anchor.current) setSelection(rectangle(anchor.current, { day: i, minute: m }, step)); }}>
                        <span className={cell && cell.total / max > 0.55 ? 'cell-num light' : 'cell-num'}>{cell ? cell.total : ''}</span>
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <div className="plan-side">
          {editable && selection.length > 0 ? (
            <CellEditor key={selection.join(',')} keys={selection} first={grid.get(selection[0]) ?? null} categories={categories} lang={lang}
              onApply={cell => update(selection, cell)} onClose={() => update(selection, null)} onClear={() => setSelection([])} />
          ) : (
            <p className="muted">{editable ? t('setup.pickCells') : t('setup.readOnly')}</p>
          )}

          {editable && (
            <div className="plan-save">
              {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
              <label className="field"><span>{t('setup.reasonOptional')}</span>
                <input value={reason} onChange={e => setReason(e.target.value)} /></label>
              <ActionButton kind="primary" disabled={!dirty || !name.trim()} onError={setError} onRun={save}>
                {plan ? t('setup.savePlan') : t('setup.createPlan')}
              </ActionButton>
              {plan && !isDefault && (
                <ActionButton kind="danger" onError={setError}
                  onRun={async () => {
                    await api.post(`/api/terminals/${terminal.terminalId}/capacity/templates/${plan.templateId}/deactivate`, { reason: reason.trim() || 'Removed in capacity setup' });
                    onSaved(t('setup.planRemoved'));
                  }}>
                  {t('setup.removePlan')}
                </ActionButton>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function CellEditor({ keys, first, categories, lang, onApply, onClose, onClear }: {
  keys: string[];
  first: Cell | null;
  categories: Category[];
  lang: string;
  onApply: (cell: Cell) => void;
  onClose: () => void;
  onClear: () => void;
}) {
  const { t } = useApp();
  const [total, setTotal] = useState(first ? String(first.total) : '');
  const [overbook, setOverbook] = useState(first ? String(first.overbookPct) : '0');
  const [quotas, setQuotas] = useState<Record<string, string>>(
    Object.fromEntries(categories.map(c => [c.code, first?.quotas[c.code] !== undefined ? String(first.quotas[c.code]) : ''])));
  const valid = total.trim() !== '' && Number.isInteger(Number(total)) && Number(total) >= 0
    && Object.values(quotas).every(q => q.trim() === '' || (Number.isInteger(Number(q)) && Number(q) >= 0));

  return (
    <div className="subform">
      <strong>{t('setup.selected', { n: keys.length })}</strong>
      <div className="two">
        <label className="field"><span>{t('setup.total')}</span>
          <input inputMode="numeric" value={total} dir="ltr" autoFocus onChange={e => setTotal(e.target.value)} /></label>
        <label className="field"><span>{t('setup.overbook')}</span>
          <input inputMode="decimal" value={overbook} dir="ltr" onChange={e => setOverbook(e.target.value)} /></label>
      </div>
      <fieldset>
        <legend className="small-legend">{t('setup.quotas')}</legend>
        <p className="hint">{t('setup.quotasHint')}</p>
        <div className="quota-list">
          {categories.map(c => (
            <label key={c.code} className="field field-row">
              <span>{categoryName(c, c.code, lang)}</span>
              <input inputMode="numeric" dir="ltr" value={quotas[c.code] ?? ''} placeholder="—"
                onChange={e => setQuotas(q => ({ ...q, [c.code]: e.target.value }))} />
            </label>
          ))}
        </div>
      </fieldset>
      <div className="actions wrap">
        <button type="button" className="btn btn-primary" disabled={!valid}
          onClick={() => onApply({
            total: Number(total),
            overbookPct: Number(overbook) || 0,
            quotas: Object.fromEntries(Object.entries(quotas).filter(([, v]) => v.trim() !== '').map(([k, v]) => [k, Number(v)])),
          })}>
          {t('setup.apply', { n: keys.length })}
        </button>
        <button type="button" className="btn btn-danger-quiet" onClick={onClose}>{t('setup.closeHours')}</button>
        <button type="button" className="link-btn" onClick={onClear}>{t('setup.clearSelection')}</button>
      </div>
    </div>
  );
}

// ================================================================== reserved places

interface Reservation {
  reservationId: number;
  companyId: number | null;
  category: string | null;
  startAt: string;
  endAt: string;
  unitsPerSlot: number;
  releaseHoursBefore: number;
  isActive: boolean;
}

function Reserved({ editable }: { editable: boolean }) {
  const { api, t, lang, terminal, today } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  const list = useLoad(() => api.get<Reservation[]>(`${base}/capacity/reservations`), [base]);
  const companies = useLoad(() => api.get<Company[]>('/api/companies'), []);
  const categories = useLoad(() => api.get<Category[]>(`${base}/categories`), [base]);
  const [companyId, setCompanyId] = useState('');
  const [category, setCategory] = useState('');
  const [fromDate, setFromDate] = useState(today);
  const [fromTime, setFromTime] = useState('06:00');
  const [toDate, setToDate] = useState(addDays(today, 30));
  const [toTime, setToTime] = useState('22:00');
  const [units, setUnits] = useState('2');
  const [release, setRelease] = useState('12');
  const [reason, setReason] = useState('');
  const [done, setDone] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const savedText = useSavedText();
  const companyName = (id: number | null) => (id === null ? t('setup.walkInReserve') : companies.data?.find(c => c.companyId === id)?.name ?? `#${id}`);
  const byCode = new Map((categories.data ?? []).map(c => [c.code, c]));

  return (
    <div className="setup-two">
      <div>
        <p className="hint">{t('setup.reservedHint')}</p>
        {list.error && <LoadError error={list.error} retry={list.reload} />}
        {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
        {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
        {list.data && list.data.length === 0 && <p className="empty">{t('setup.noReserved')}</p>}
        {list.data && list.data.length > 0 && (
          <div className="table-wrap">
            <table className="grid compact">
              <thead><tr><th>{t('setup.for')}</th><th>{t('slots.scope')}</th><th>{t('setup.period')}</th><th>{t('setup.unitsPerSlot')}</th><th>{t('setup.releaseBefore')}</th><th /></tr></thead>
              <tbody>
                {list.data.map(r => (
                  <tr key={r.reservationId}>
                    <td>{companyName(r.companyId)}</td>
                    <td>{r.category ? categoryName(byCode.get(r.category), r.category, lang) : t('slots.scopeAll')}</td>
                    <td className="num">{stamp(r.startAt, terminal.timeZone, lang)} – {stamp(r.endAt, terminal.timeZone, lang)}</td>
                    <td className="num">{r.unitsPerSlot}</td>
                    <td className="num">{t('setup.hours', { n: r.releaseHoursBefore })}</td>
                    <td className="cell-action">
                      {editable && (
                        <ActionButton onError={setError} onRun={async () => {
                          await api.post(`${base}/capacity/reservations/${r.reservationId}/deactivate`, { reason: 'Removed in capacity setup' });
                          setDone(t('slots.removed'));
                          list.reload();
                        }}>{t('setup.remove')}</ActionButton>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {editable && (
        <form className="book-side form-stack" onSubmit={e => e.preventDefault()}>
          <h2>{t('setup.addReserved')}</h2>
          <label className="field"><span>{t('setup.for')}</span>
            <select value={companyId} onChange={e => setCompanyId(e.target.value)}>
              <option value="">{t('setup.walkInReserve')}</option>
              {(companies.data ?? []).map(c => <option key={c.companyId} value={c.companyId}>{c.name}</option>)}
            </select></label>
          <label className="field"><span>{t('slots.scope')}</span>
            <select value={category} onChange={e => setCategory(e.target.value)}>
              <option value="">{t('slots.scopeAll')}</option>
              {(categories.data ?? []).map(c => <option key={c.code} value={c.code}>{categoryName(c, c.code, lang)}</option>)}
            </select></label>
          <div className="two">
            <label className="field"><span>{t('setup.from')}</span><input type="date" value={fromDate} onChange={e => setFromDate(e.target.value)} /></label>
            <label className="field"><span>&nbsp;</span><input type="time" value={fromTime} step={terminal.slotMinutes * 60} onChange={e => setFromTime(e.target.value)} /></label>
            <label className="field"><span>{t('setup.to')}</span><input type="date" value={toDate} onChange={e => setToDate(e.target.value)} /></label>
            <label className="field"><span>&nbsp;</span><input type="time" value={toTime} step={terminal.slotMinutes * 60} onChange={e => setToTime(e.target.value)} /></label>
            <label className="field"><span>{t('setup.unitsPerSlot')}</span><input inputMode="numeric" dir="ltr" value={units} onChange={e => setUnits(e.target.value)} /></label>
            <label className="field"><span>{t('setup.releaseBefore')}</span><input inputMode="numeric" dir="ltr" value={release} onChange={e => setRelease(e.target.value)} /></label>
          </div>
          <p className="hint">{t('setup.releaseHint')}</p>
          <label className="field"><span>{t('setup.reasonOptional')}</span><input value={reason} onChange={e => setReason(e.target.value)} /></label>
          <ActionButton kind="primary" onError={setError} onRun={async () => {
            const response = await api.post<AdminResponse<Reservation>>(`${base}/capacity/reservations`, {
              companyId: companyId ? Number(companyId) : null,
              category: category || null,
              startAt: zonedIso(fromDate, fromTime, terminal.timeZone),
              endAt: zonedIso(toDate, toTime, terminal.timeZone),
              unitsPerSlot: Number(units),
              releaseHoursBefore: Number(release),
              reason: reason.trim() || null,
            });
            setDone(savedText(response.regeneratedSlots));
            setReason('');
            list.reload();
          }}>{t('setup.addReserved')}</ActionButton>
        </form>
      )}
    </div>
  );
}

// ================================================================== rules & settings

const settingKeys = ['early_grace_min', 'late_grace_min', 'cancel_cutoff_min', 'max_appt_per_truck_per_day'] as const;
const settingText = {
  early_grace_min: 'setup.earlyGrace',
  late_grace_min: 'setup.lateGrace',
  cancel_cutoff_min: 'setup.cancelCutoff',
  max_appt_per_truck_per_day: 'setup.maxPerTruck',
} as const;

function Rules({ editable }: { editable: boolean }) {
  const { api, t, lang, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  const rules = useLoad(() => api.get<{ category: string; hoursBefore: number }[]>(`${base}/capacity/release-rules`), [base]);
  const settings = useLoad(() => api.get<Record<string, number>>(`${base}/settings`), [base]);
  const categories = useLoad(() => api.get<Category[]>(`${base}/categories`), [base]);
  const [hours, setHours] = useState<Record<string, string> | null>(null);
  const [values, setValues] = useState<Record<string, string> | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const savedText = useSavedText();

  useEffect(() => {
    if (rules.data && categories.data && hours === null)
      setHours(Object.fromEntries(categories.data.map(c => [c.code, String(rules.data!.find(r => r.category === c.code)?.hoursBefore ?? '')])));
  }, [rules.data, categories.data, hours]);
  useEffect(() => {
    if (settings.data && values === null) setValues(Object.fromEntries(settingKeys.map(k => [k, String(settings.data![k] ?? '')])));
  }, [settings.data, values]);

  return (
    <div className="setup-two">
      <div className="book-side form-stack">
        <h2>{t('setup.releaseRules')}</h2>
        <p className="hint">{t('setup.releaseRulesHint')}</p>
        {hours && (categories.data ?? []).map(c => (
          <label key={c.code} className="field field-row">
            <span>{categoryName(c, c.code, lang)}</span>
            <input inputMode="numeric" dir="ltr" placeholder="—" value={hours[c.code] ?? ''} disabled={!editable}
              onChange={e => setHours(h => ({ ...h!, [c.code]: e.target.value }))} />
          </label>
        ))}
        {editable && hours && (
          <ActionButton kind="primary" onError={setError} onRun={async () => {
            const response = await api.put<AdminResponse<unknown>>(`${base}/capacity/release-rules`, {
              rules: Object.entries(hours).filter(([, v]) => v.trim() !== '').map(([category, v]) => ({ category, hoursBefore: Number(v) })),
              reason: null,
            });
            setDone(savedText(response.regeneratedSlots));
          }}>{t('setup.saveRules')}</ActionButton>
        )}
      </div>

      <div className="book-side form-stack">
        <h2>{t('setup.settings')}</h2>
        {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
        {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
        {values && settingKeys.map(k => (
          <label key={k} className="field field-row">
            <span>{t(settingText[k])}</span>
            <input inputMode="numeric" dir="ltr" value={values[k]} disabled={!editable} onChange={e => setValues(v => ({ ...v!, [k]: e.target.value }))} />
          </label>
        ))}
        {editable && values && (
          <ActionButton kind="primary" onError={setError} onRun={async () => {
            await api.put(`${base}/settings`, { values: Object.fromEntries(Object.entries(values).map(([k, v]) => [k, Number(v)])), reason: null });
            setDone(t('slots.applied'));
          }}>{t('setup.saveSettings')}</ActionButton>
        )}
      </div>
    </div>
  );
}
