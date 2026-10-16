import { useMemo, useState, type ReactNode } from 'react';
import type { TextKey } from '../lib/i18n.ts';
import { addDays } from '../lib/time.ts';
import type { AdminResponse, Category, Country } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, Notice, useLoad } from '../ui/kit.tsx';

interface SetupStep {
  key: StepKey;
  done: boolean;
  optional: boolean;
  detail: string;
  count: number | null;
  count2: number | null;
}

interface SetupStatus {
  terminalId: number;
  code: string;
  readyToBook: boolean;
  steps: SetupStep[];
  adapter: string;
  tosConnected: boolean;
}

interface Lane { laneId: number; gateCode: string; laneCode: string; direction: 'In' | 'Out'; isActive: boolean }

interface OperatorRow { operatorId: number; name: string; isActive: boolean }

type StepKey = 'moveTypes' | 'weeklyPlan' | 'slots' | 'lanes' | 'tos' | 'staff' | 'companies' | 'map';

const titles: Record<StepKey, TextKey> = {
  moveTypes: 'onboard.moveTypes', weeklyPlan: 'onboard.weeklyPlan', slots: 'onboard.slots', lanes: 'onboard.lanes',
  tos: 'onboard.tos', staff: 'onboard.staff', companies: 'onboard.companies', map: 'onboard.map',
};

/**
 * Bringing a terminal onto Yardsyncs: the checklist for the chosen terminal, in the order the work is done, with the
 * quick actions for each step; and, for system and operator admins, adding a new terminal.
 */
export function Onboard() {
  const { api, t, user, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  const status = useLoad(() => api.get<SetupStatus>(`${base}/setup`), [base]);
  // undefined: the first step still to do is open; null: all closed.
  const [open, setOpen] = useState<StepKey | null | undefined>(undefined);
  const [done, setDone] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const canAdd = user.role === 'SysAdmin' || user.role === 'OperatorAdmin';

  function changed(message?: string) {
    if (message) setDone(message);
    status.reload();
  }

  const s = status.data;
  return (
    <section className="screen">
      <header className="screen-head">
        <h1>{t('onboard.title')}</h1>
        {s && <p className={s.readyToBook ? 'ready-badge ok' : 'ready-badge'}>{s.readyToBook ? t('onboard.ready') : t('onboard.notReady')}</p>}
      </header>
      {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {status.error && <LoadError error={status.error} retry={status.reload} />}

      <div className="setup-two">
        <div>
          <p className="hint">{t('onboard.lead', { terminal: terminal.name })}</p>
          {s && (
            <ol className="checklist">
              {s.steps.map(step => {
                const expanded = open === undefined ? s.steps.find(x => !x.done) === step : open === step.key;
                return (
                  <li key={step.key} className={step.done ? 'step done' : 'step'}>
                    <div className="step-head">
                      <span className="step-state">{step.done ? t('onboard.done') : step.optional ? t('onboard.optional') : t('onboard.todo')}</span>
                      <button type="button" className="step-title" aria-expanded={expanded}
                        onClick={() => setOpen(expanded ? null : step.key)}>
                        {t(titles[step.key])}
                      </button>
                    </div>
                    <p className="step-detail">{describe(step, s, t)}</p>
                    {expanded && (
                      <div className="step-body">
                        <StepActions step={step} status={s} onChanged={changed} onError={setError} />
                      </div>
                    )}
                  </li>
                );
              })}
            </ol>
          )}
        </div>
        {canAdd && <NewTerminal onError={setError} />}
      </div>
    </section>
  );
}

function describe(step: SetupStep, s: SetupStatus, t: ReturnType<typeof useApp>['t']): string {
  const n = step.count ?? 0;
  switch (step.key) {
    case 'moveTypes': return t('onboard.moveTypes.detail', { n });
    case 'weeklyPlan': return step.done ? t('onboard.weeklyPlan.done') : t('onboard.weeklyPlan.none');
    case 'slots': return t('onboard.slots.detail', { n });
    case 'lanes': return t('onboard.lanes.detail', { in: n, out: step.count2 ?? 0 });
    case 'tos':
      return s.adapter === 'Fake' ? t('onboard.tos.fake', { code: s.code })
        : !s.tosConnected ? t('onboard.tos.notConnected', { code: s.code })
        : t('onboard.tos.connected', { adapter: s.adapter });
    case 'staff': return t('onboard.staff.detail', { n });
    case 'companies': return t('onboard.companies.detail', { n });
    case 'map': return t('onboard.map.detail', { n });
  }
}

function StepActions({ step, status, onChanged, onError }: {
  step: SetupStep; status: SetupStatus; onChanged: (message?: string) => void; onError: (m: string) => void;
}): ReactNode {
  const { t } = useApp();
  switch (step.key) {
    case 'moveTypes': return <MoveTypes onChanged={onChanged} onError={onError} />;
    case 'weeklyPlan': return step.done ? <a className="btn btn-secondary" href="#/setup">{t('onboard.openSetup')}</a> : <StarterPlan onChanged={onChanged} onError={onError} />;
    case 'slots': return <Slots onChanged={onChanged} onError={onError} />;
    case 'lanes': return <Lanes onChanged={onChanged} onError={onError} />;
    case 'tos': return <TosTest status={status} onError={onError} />;
    case 'staff': return <a className="btn btn-secondary" href="#/fleet?tab=users">{t('onboard.openUsers')}</a>;
    case 'companies': return <a className="btn btn-secondary" href="#/fleet?tab=companies">{t('onboard.openCompanies')}</a>;
    case 'map': return <a className="btn btn-secondary" href="#/map">{t('onboard.openMap')}</a>;
  }
}

function MoveTypes({ onChanged, onError }: { onChanged: (m?: string) => void; onError: (m: string) => void }) {
  const { api, t, lang, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  const list = useLoad(() => api.get<Category[]>(`${base}/categories`), [base]);
  const [form, setForm] = useState({ code: '', nameEn: '', nameAr: '', mode: 'Container' as 'Container' | 'Cargo' });
  return (
    <>
      {list.data && list.data.length > 0 && (
        <ul className="chips">{list.data.map(c => <li key={c.code}><bdi>{c.code}</bdi> {lang === 'ar' ? c.nameAr : c.nameEn}</li>)}</ul>
      )}
      <div className="actions wrap">
        <ActionButton kind="primary" onError={onError} onRun={async () => {
          const r = await api.post<AdminResponse<number>>(`${base}/setup/standard-move-types`);
          list.reload();
          onChanged(t('onboard.addedStandard', { n: r.data }));
        }}>{t('onboard.addStandard')}</ActionButton>
      </div>
      <form className="inline-form" onSubmit={e => e.preventDefault()}>
        <label className="field field-short"><span>{t('onboard.code')}</span>
          <input dir="ltr" value={form.code} placeholder="IMP_REEFER" onChange={e => setForm({ ...form, code: e.target.value.toUpperCase() })} /></label>
        <label className="field"><span>{t('onboard.nameEn')}</span><input dir="ltr" value={form.nameEn} onChange={e => setForm({ ...form, nameEn: e.target.value })} /></label>
        <label className="field"><span>{t('onboard.nameAr')}</span><input dir="rtl" value={form.nameAr} onChange={e => setForm({ ...form, nameAr: e.target.value })} /></label>
        <label className="field field-short"><span>{t('onboard.mode')}</span>
          <select value={form.mode} onChange={e => setForm({ ...form, mode: e.target.value as 'Container' | 'Cargo' })}>
            <option value="Container">{t('onboard.mode.Container')}</option>
            <option value="Cargo">{t('onboard.mode.Cargo')}</option>
          </select></label>
        <ActionButton kind="secondary" disabled={!form.code.trim() || !form.nameEn.trim() || !form.nameAr.trim()} onError={onError} onRun={async () => {
          await api.post(`${base}/categories`, {
            code: form.code.trim(), nameEn: form.nameEn.trim(), nameAr: form.nameAr.trim(), mode: form.mode,
            unitType: form.mode === 'Cargo' ? 'Trip' : 'Move', sortOrder: (list.data?.length ?? 0) + 1,
          });
          setForm({ code: '', nameEn: '', nameAr: '', mode: 'Container' });
          list.reload();
          onChanged();
        }}>{t('onboard.addMoveType')}</ActionButton>
      </form>
    </>
  );
}

function StarterPlan({ onChanged, onError }: { onChanged: (m?: string) => void; onError: (m: string) => void }) {
  const { api, t, lang, terminal } = useApp();
  const [from, setFrom] = useState('06:00');
  const [to, setTo] = useState('22:00');
  const [trucks, setTrucks] = useState('20');
  const [days, setDays] = useState<number[]>([0, 1, 2, 3, 4, 5, 6]);
  const names = useMemo(() => [0, 1, 2, 3, 4, 5, 6].map(i => new Intl.DateTimeFormat(lang === 'ar' ? 'ar-u-nu-latn' : 'en-GB', { weekday: 'short', timeZone: 'UTC' })
    .format(new Date(Date.UTC(2026, 9, 18 + i)))), [lang]); // 18 Oct 2026 is a Sunday
  const count = Number(trucks);
  return (
    <form className="form-stack" onSubmit={e => e.preventDefault()}>
      <p className="hint">{t('onboard.planHint')}</p>
      <div className="inline-form">
        <label className="field field-short"><span>{t('onboard.planFrom')}</span><input type="time" step={1800} value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label className="field field-short"><span>{t('onboard.planTo')}</span><input type="time" step={1800} value={to} onChange={e => setTo(e.target.value)} /></label>
        <label className="field field-short"><span>{t('onboard.planTrucks')}</span><input inputMode="numeric" dir="ltr" value={trucks} onChange={e => setTrucks(e.target.value)} /></label>
      </div>
      <fieldset className="day-picks">
        <legend>{t('onboard.planDays')}</legend>
        {names.map((name, i) => (
          <label key={i} className="radio">
            <input type="checkbox" checked={days.includes(i)} onChange={e => setDays(e.target.checked ? [...days, i].sort() : days.filter(d => d !== i))} /> {name}
          </label>
        ))}
      </fieldset>
      <div className="actions wrap">
        <ActionButton kind="primary" disabled={days.length === 0 || !(count > 0) || !from || !to || to <= from} onError={onError} onRun={async () => {
          await api.post(`/api/terminals/${terminal.terminalId}/capacity/templates`, {
            name: t('onboard.planName'), isDefault: true, validFrom: null, validTo: null, priority: 0,
            windows: days.map(day => ({ day, start: from, end: to, totalCapacity: count })),
            reason: 'Starter plan from the terminal set-up',
          });
          onChanged(t('onboard.planMade'));
        }}>{t('onboard.makePlan')}</ActionButton>
        <a className="btn btn-secondary" href="#/setup">{t('onboard.openSetup')}</a>
      </div>
    </form>
  );
}

function Slots({ onChanged, onError }: { onChanged: (m?: string) => void; onError: (m: string) => void }) {
  const { api, t, terminal, today } = useApp();
  return (
    <div className="actions wrap">
      <ActionButton kind="primary" onError={onError} onRun={async () => {
        const to = addDays(today, terminal.bookingHorizonDays);
        await api.post(`/api/terminals/${terminal.terminalId}/slots/generate?from=${today}&to=${to}`);
        onChanged(t('onboard.slotsMade'));
      }}>{t('onboard.makeSlots')}</ActionButton>
      <a className="btn btn-secondary" href="#/slots">{t('onboard.openSlots')}</a>
    </div>
  );
}

function Lanes({ onChanged, onError }: { onChanged: (m?: string) => void; onError: (m: string) => void }) {
  const { api, t, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/gate/lanes`;
  const list = useLoad(() => api.get<Lane[]>(base), [base]);
  const [form, setForm] = useState({ gateCode: 'MAIN', laneCode: '', direction: 'In' as 'In' | 'Out' });
  return (
    <>
      {list.data && list.data.length > 0 && (
        <ul className="chips">
          {list.data.map(l => <li key={l.laneId} className={l.isActive ? undefined : 'inactive'}><bdi>{l.gateCode} {l.laneCode}</bdi> · {t(l.direction === 'In' ? 'onboard.direction.In' : 'onboard.direction.Out')}</li>)}
        </ul>
      )}
      <form className="inline-form" onSubmit={e => e.preventDefault()}>
        <label className="field field-short"><span>{t('onboard.gateCode')}</span>
          <input dir="ltr" value={form.gateCode} onChange={e => setForm({ ...form, gateCode: e.target.value.toUpperCase() })} /></label>
        <label className="field field-short"><span>{t('onboard.laneCode')}</span>
          <input dir="ltr" value={form.laneCode} placeholder="IN-2" onChange={e => setForm({ ...form, laneCode: e.target.value.toUpperCase() })} /></label>
        <label className="field field-short"><span>{t('onboard.direction')}</span>
          <select value={form.direction} onChange={e => setForm({ ...form, direction: e.target.value as 'In' | 'Out' })}>
            <option value="In">{t('onboard.direction.In')}</option>
            <option value="Out">{t('onboard.direction.Out')}</option>
          </select></label>
        <ActionButton kind="secondary" disabled={!form.gateCode.trim() || !form.laneCode.trim()} onError={onError} onRun={async () => {
          await api.post(base, { gateCode: form.gateCode.trim(), laneCode: form.laneCode.trim(), direction: form.direction });
          setForm({ ...form, laneCode: '' });
          list.reload();
          onChanged();
        }}>{t('onboard.addLane')}</ActionButton>
      </form>
    </>
  );
}

interface TosTestResult { adapter: string; reached: boolean; message: string; milliseconds: number }

function TosTest({ status, onError }: { status: SetupStatus; onError: (m: string) => void }) {
  const { api, t } = useApp();
  const [result, setResult] = useState<TosTestResult | null>(null);
  return (
    <>
      <div className="actions wrap">
        <ActionButton kind="primary" onError={onError} onRun={async () => {
          setResult((await api.post<AdminResponse<TosTestResult>>(`/api/terminals/${status.terminalId}/setup/tos-test`)).data);
        }}>{t('onboard.testTos')}</ActionButton>
        <a className="btn btn-secondary" href="#/docs/tos-adapter" target="_blank" rel="noreferrer">{t('onboard.tosDocs')}</a>
      </div>
      {result && (
        <p className={result.reached ? 'ok-text' : 'check-fail'} role="status">
          {result.reached ? t('onboard.tosOk', { adapter: result.adapter, ms: result.milliseconds }) : t('onboard.tosDown', { adapter: result.adapter, message: result.message })}
        </p>
      )}
    </>
  );
}

function NewTerminal({ onError }: { onError: (m: string) => void }) {
  const { api, t, user } = useApp();
  const country = useLoad(() => api.get<Country>('/api/country'), []).data;
  const operators = useLoad(() => (user.role === 'SysAdmin' ? api.get<OperatorRow[]>('/api/operators') : Promise.resolve([] as OperatorRow[])), [user.role]);
  const [form, setForm] = useState({
    code: '', name: '', terminalType: 'Container', timeZone: '', slotMinutes: '60', bookingHorizonDays: '14', operatorId: '', starter: true,
  });
  const timeZone = form.timeZone || country?.timeZone || '';
  return (
    <form className="book-side form-stack" onSubmit={e => e.preventDefault()}>
      <h2>{t('onboard.newTitle')}</h2>
      <p className="hint">{t('onboard.newHint')}</p>
      <div className="two">
        <label className="field field-short"><span>{t('onboard.code')}</span>
          <input dir="ltr" maxLength={20} value={form.code} placeholder="JED2" onChange={e => setForm({ ...form, code: e.target.value.toUpperCase() })} /></label>
        <label className="field"><span>{t('onboard.type')}</span>
          <select value={form.terminalType} onChange={e => setForm({ ...form, terminalType: e.target.value })}>
            <option value="Container">{t('onboard.type.Container')}</option>
            <option value="Multipurpose">{t('onboard.type.Multipurpose')}</option>
          </select></label>
      </div>
      <label className="field"><span>{t('onboard.terminalName')}</span>
        <input maxLength={150} value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} /></label>
      <label className="field"><span>{t('onboard.timeZone')}</span>
        <input dir="ltr" value={timeZone} placeholder="Asia/Riyadh" onChange={e => setForm({ ...form, timeZone: e.target.value })} /></label>
      <div className="two">
        <label className="field"><span>{t('onboard.slotMinutes')}</span>
          <select value={form.slotMinutes} onChange={e => setForm({ ...form, slotMinutes: e.target.value })}>
            {['15', '30', '60', '120'].map(m => <option key={m} value={m}>{m}</option>)}
          </select></label>
        <label className="field"><span>{t('onboard.horizon')}</span>
          <input inputMode="numeric" dir="ltr" value={form.bookingHorizonDays} onChange={e => setForm({ ...form, bookingHorizonDays: e.target.value })} /></label>
      </div>
      {user.role === 'SysAdmin' && (operators.data?.length ?? 0) > 0 && (
        <label className="field"><span>{t('onboard.operator')}</span>
          <select value={form.operatorId} onChange={e => setForm({ ...form, operatorId: e.target.value })}>
            <option value="">{t('onboard.noOperator')}</option>
            {operators.data!.filter(o => o.isActive).map(o => <option key={o.operatorId} value={o.operatorId}>{o.name}</option>)}
          </select></label>
      )}
      <label className="radio"><input type="checkbox" checked={form.starter} onChange={e => setForm({ ...form, starter: e.target.checked })} /> {t('onboard.starter')}</label>
      <ActionButton kind="primary" disabled={form.code.trim().length < 2 || !form.name.trim()} onError={onError} onRun={async () => {
        const created = await api.post<AdminResponse<{ terminalId: number }>>('/api/terminals', {
          code: form.code.trim(), name: form.name.trim(), terminalType: form.terminalType, timeZone: timeZone || null,
          slotMinutes: Number(form.slotMinutes), bookingHorizonDays: Number(form.bookingHorizonDays),
          operatorId: form.operatorId ? Number(form.operatorId) : null, starter: form.starter,
        });
        // Open the new terminal's checklist (the terminal list is read when the portal starts).
        try { localStorage.setItem('yardsyncs.terminal', String(created.data.terminalId)); } catch { /* ignore */ }
        window.location.hash = '#/onboard';
        window.location.reload();
      }}>{t('onboard.create')}</ActionButton>
    </form>
  );
}
