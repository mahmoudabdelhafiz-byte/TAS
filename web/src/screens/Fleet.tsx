import { useState } from 'react';
import { contrastWithWhite, defaultBrand } from '../lib/brand.ts';
import { languageName, messageLanguages } from '../lib/languages.ts';
import { isAdminRole, isCompanyRole } from '../lib/routes.ts';
import { stamp } from '../lib/time.ts';
import type { Company, Country, Driver, Terminal, Truck, UserRole } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, Notice, useLoad } from '../ui/kit.tsx';

interface UserRow {
  userId: number;
  email: string;
  fullName: string;
  role: UserRole;
  companyId: number | null;
  terminalId: number | null;
  mobile: string | null;
  preferredLanguage: string;
  isActive: boolean;
  lastLoginAt: string | null;
}

interface CompanyRow extends Company {
  crNumber: string | null;
  companyType: string;
  isPriority: boolean;
  noShowCount: number;
}

interface OperatorRow {
  operatorId: number;
  code: string;
  name: string;
  isActive: boolean;
  terminals: { terminalId: number; code: string; name: string }[];
  admins: number;
}

type Tab = 'trucks' | 'drivers' | 'users' | 'apikeys' | 'companies' | 'operators';
const tabText = { trucks: 'fleet.trucks', drivers: 'fleet.drivers', users: 'fleet.users', apikeys: 'fleet.apiKeys', companies: 'fleet.companies', operators: 'fleet.operators' } as const;

/** Trucks, drivers and users of a company; companies for terminal admins. */
export function Fleet() {
  const { api, t, user, route, go } = useApp();
  const companyUser = isCompanyRole(user.role);
  const terminalAdmin = isAdminRole(user.role);
  const operators = user.role === 'SysAdmin' || user.role === 'OperatorAdmin';
  const tabs: Tab[] = terminalAdmin ? ['companies', 'trucks', 'drivers', 'users', 'apikeys', ...(operators ? ['operators' as const] : [])]
    : user.role === 'CompanyAdmin' ? ['trucks', 'drivers', 'users', 'apikeys'] : ['trucks', 'drivers'];
  const tab: Tab = tabs.includes(route.params.tab as Tab) ? (route.params.tab as Tab) : tabs[0];
  const companies = useLoad(() => (companyUser ? Promise.resolve([] as CompanyRow[]) : api.get<CompanyRow[]>('/api/companies')), [companyUser]);
  const [picked, setPicked] = useState<string>('');
  const companyId = companyUser ? user.companyId! : picked ? Number(picked) : (companies.data?.[0]?.companyId ?? null);
  const canEdit = terminalAdmin || (user.role === 'CompanyAdmin');

  return (
    <section className="screen">
      <header className="screen-head">
        <h1>{companyUser ? t('fleet.titleCompany') : t('fleet.title')}</h1>
        <div className="segmented" role="tablist">
          {tabs.map(x => (
            <button key={x} type="button" role="tab" aria-selected={tab === x} className={tab === x ? 'seg active' : 'seg'}
              onClick={() => go('fleet', { tab: x })}>{t(tabText[x])}</button>
          ))}
        </div>
        {!companyUser && tab !== 'companies' && tab !== 'operators' && (
          <label className="field field-inline">
            <span>{t('common.company')}</span>
            <select value={companyId ?? ''} onChange={e => setPicked(e.target.value)}>
              {tab === 'users' && <option value="-1">{t('fleet.terminalStaff')}</option>}
              {(companies.data ?? []).map(c => <option key={c.companyId} value={c.companyId}>{c.name}</option>)}
            </select>
          </label>
        )}
      </header>

      {tab === 'companies' && <Companies list={companies.data ?? []} reload={companies.reload} />}
      {tab === 'operators' && <Operators />}
      {tab === 'trucks' && companyId && companyId > 0 && <Trucks companyId={companyId} canEdit={canEdit} canBlock={terminalAdmin} />}
      {tab === 'drivers' && companyId && companyId > 0 && <Drivers companyId={companyId} canEdit={canEdit} canBlock={terminalAdmin} />}
      {tab === 'users' && companyId && <Users companyId={companyId} canEdit={canEdit} />}
      {tab === 'apikeys' && companyId && companyId > 0 && <ApiKeys companyId={companyId} />}
      {tab !== 'companies' && tab !== 'operators' && companyId && companyId < 0 && tab !== 'users' && <p className="muted">{t('common.chooseCompany')}</p>}
    </section>
  );
}

function useMessages() {
  const [done, setDone] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const view = (
    <>
      {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
    </>
  );
  return { setDone, setError, view };
}

/** Block / unblock with a reason, shown inline in the row. */
function BlockButton({ blocked, onSet, onError, label }: { blocked: boolean; onSet: (blocked: boolean, reason: string) => Promise<void>; onError: (m: string) => void; label?: string }) {
  const { t } = useApp();
  const [asking, setAsking] = useState(false);
  const [reason, setReason] = useState('');
  if (blocked) return <ActionButton onError={onError} onRun={() => onSet(false, 'Unblocked')}>{t('fleet.unblock')}</ActionButton>;
  if (!asking) return <button type="button" className="btn btn-danger-quiet" onClick={() => setAsking(true)}>{label ?? t('fleet.block')}</button>;
  return (
    <span className="inline-ask">
      <input value={reason} placeholder={t('common.reason')} onChange={e => setReason(e.target.value)} autoFocus />
      <ActionButton kind="danger" disabled={!reason.trim()} onError={onError} onRun={async () => { await onSet(true, reason.trim()); setAsking(false); }}>
        {label ?? t('fleet.block')}
      </ActionButton>
    </span>
  );
}

function Trucks({ companyId, canEdit, canBlock }: { companyId: number; canEdit: boolean; canBlock: boolean }) {
  const { api, t } = useApp();
  const base = `/api/companies/${companyId}/trucks`;
  const list = useLoad(() => api.get<Truck[]>(base), [base]);
  const [plate, setPlate] = useState('');
  const m = useMessages();
  return (
    <div className="setup-two">
      <div>
        {m.view}
        {list.error && <LoadError error={list.error} retry={list.reload} />}
        {list.data && list.data.length === 0 && <p className="empty">{t('fleet.noTrucks')}</p>}
        {list.data && list.data.length > 0 && (
          <div className="table-wrap">
            <table className="grid compact">
              <thead><tr><th>{t('fleet.plate')}</th><th>{t('appt.status')}</th><th /></tr></thead>
              <tbody>
                {list.data.map(x => (
                  <tr key={x.truckId}>
                    <td className="plate"><bdi>{x.plateNumber}</bdi></td>
                    <td>{x.isBlocked ? <span className="check-fail">{t('fleet.blocked')}</span> : <span className="ok-text">{t('fleet.active')}</span>}</td>
                    <td className="cell-action">
                      {canBlock && <BlockButton blocked={x.isBlocked} onError={m.setError}
                        onSet={async (blocked, reason) => { await api.post(`${base}/${x.truckId}/block`, { blocked, reason }); list.reload(); }} />}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {canEdit && (
        <form className="book-side form-stack" onSubmit={e => e.preventDefault()}>
          <h2>{t('fleet.addTruck')}</h2>
          <label className="field"><span>{t('fleet.plate')}</span>
            <input value={plate} onChange={e => setPlate(e.target.value)} placeholder="1234 ABJ / ١٢٣٤ أ ب ح" /></label>
          <p className="hint">{t('fleet.plateHint')}</p>
          <ActionButton kind="primary" disabled={!plate.trim()} onError={m.setError} onRun={async () => {
            await api.post(base, { plateNumber: plate.trim() });
            setPlate('');
            m.setDone(t('fleet.saved'));
            list.reload();
          }}>{t('fleet.addTruck')}</ActionButton>
        </form>
      )}
    </div>
  );
}


/** A driver whose personal data was erased keeps this ID number. */
const erased = (x: { idNumber: string }) => x.idNumber.startsWith('ERASED-');

/** Saudi PDPL: a copy of the driver's data, and erasure (with a reason, after a warning). */
function PersonalData({ url, erased, onError, onErased }: { url: string; erased: boolean; onError: (m: string) => void; onErased: () => void }) {
  const { api, t } = useApp();
  const [asking, setAsking] = useState(false);
  const [reason, setReason] = useState('');
  const copy = (
    <ActionButton onError={onError} title={t('fleet.personalDataHint')} onRun={async () => {
      const { blob, fileName } = await api.download(`${url}/personal-data`);
      const href = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = href;
      a.download = fileName ?? `driver-${url.split('/').pop()}-personal-data.json`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(href), 1000);
    }}>{t('fleet.personalData')}</ActionButton>
  );
  if (erased) return copy;
  if (!asking) return <>{copy}<button type="button" className="btn btn-danger-quiet" onClick={() => setAsking(true)}>{t('fleet.erase')}</button></>;
  return (
    <div className="erase-ask" role="group" aria-label={t('fleet.erase')}>
      <p className="warn-text">{t('fleet.eraseWarn')}</p>
      <span className="inline-ask">
        <input value={reason} placeholder={t('fleet.eraseReason')} onChange={e => setReason(e.target.value)} autoFocus />
        <ActionButton kind="danger" disabled={!reason.trim()} onError={onError} onRun={async () => {
          await api.post(`${url}/erase`, { reason: reason.trim() });
          setAsking(false);
          onErased();
        }}>{t('fleet.erase')}</ActionButton>
        <button type="button" className="btn btn-secondary" onClick={() => setAsking(false)}>{t('fleet.keepData')}</button>
      </span>
    </div>
  );
}

function Drivers({ companyId, canEdit, canBlock }: { companyId: number; canEdit: boolean; canBlock: boolean }) {
  const { api, t, lang } = useApp();
  const base = `/api/companies/${companyId}/drivers`;
  const list = useLoad(() => api.get<(Driver & { idNumber: string })[]>(base), [base]);
  // The country's ID document and how its mobiles are typed (Saudi Arabia: ID / Iqama, 05…).
  const country = useLoad(() => api.get<Country>('/api/country'), []).data;
  const [form, setForm] = useState({ fullName: '', idNumber: '', mobile: '', preferredLanguage: 'ar' });
  const m = useMessages();
  return (
    <div className="setup-two">
      <div>
        {m.view}
        {list.error && <LoadError error={list.error} retry={list.reload} />}
        {list.data && list.data.length === 0 && <p className="empty">{t('fleet.noDrivers')}</p>}
        {list.data && list.data.length > 0 && (
          <div className="table-wrap">
            <table className="grid compact">
              <thead><tr><th>{t('fleet.name')}</th><th>{t('fleet.mobile')}</th><th>{t('fleet.language')}</th><th>{t('appt.status')}</th><th /></tr></thead>
              <tbody>
                {list.data.map(x => (
                  <tr key={x.driverId}>
                    <td>{x.fullName}</td>
                    <td className="num"><bdi>{x.mobile}</bdi></td>
                    <td>{languageName(x.preferredLanguage.trim())}</td>
                    <td>{erased(x) ? <span className="muted">{t('fleet.erased')}</span>
                      : x.isBlocked ? <span className="check-fail">{t('fleet.blocked')}</span> : <span className="ok-text">{t('fleet.active')}</span>}</td>
                    <td className="cell-action">
                      {canBlock && !erased(x) && <BlockButton blocked={x.isBlocked} onError={m.setError}
                        onSet={async (blocked, reason) => { await api.post(`${base}/${x.driverId}/block`, { blocked, reason }); list.reload(); }} />}
                      {canEdit && (
                        <PersonalData url={`${base}/${x.driverId}`} erased={erased(x)} onError={m.setError}
                          onErased={() => { m.setDone(t('fleet.erasedNote')); list.reload(); }} />
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {canEdit && (
        <form className="book-side form-stack" onSubmit={e => e.preventDefault()}>
          <h2>{t('fleet.addDriver')}</h2>
          <label className="field"><span>{t('fleet.name')}</span>
            <input value={form.fullName} onChange={e => setForm({ ...form, fullName: e.target.value })} /></label>
          <label className="field"><span>{country ? (lang === 'ar' ? country.idNameAr : country.idNameEn) : t('fleet.idNumber')}</span>
            <input value={form.idNumber} dir="ltr" onChange={e => setForm({ ...form, idNumber: e.target.value })} /></label>
          <label className="field"><span>{t('fleet.mobile')}</span>
            <input value={form.mobile} dir="ltr" inputMode="tel" placeholder={country?.mobileExample ?? ''} onChange={e => setForm({ ...form, mobile: e.target.value })} /></label>
          <label className="field"><span>{t('fleet.messagesIn')}</span>
            <select value={form.preferredLanguage} onChange={e => setForm({ ...form, preferredLanguage: e.target.value })}>
              {messageLanguages().map(l => <option key={l.code} value={l.code}>{l.name}</option>)}
            </select></label>
          <ActionButton kind="primary" disabled={!form.fullName.trim() || !form.idNumber.trim() || !form.mobile.trim()} onError={m.setError}
            onRun={async () => {
              await api.post(base, form);
              setForm({ fullName: '', idNumber: '', mobile: '', preferredLanguage: 'ar' });
              m.setDone(t('fleet.saved'));
              list.reload();
            }}>{t('fleet.addDriver')}</ActionButton>
        </form>
      )}
    </div>
  );
}

function Users({ companyId, canEdit }: { companyId: number; canEdit: boolean }) {
  const { api, t, tx, lang, user, terminal } = useApp();
  const staff = companyId < 0;
  const query = staff ? `terminalId=${terminal.terminalId}` : `companyId=${companyId}`;
  const list = useLoad(() => api.get<UserRow[]>(`/api/users?${query}`), [query]);
  const roles: UserRole[] = staff ? ['GateClerk', 'Planner', 'TerminalAdmin'] : ['CompanyUser', 'CompanyAdmin'];
  const [form, setForm] = useState({ email: '', fullName: '', role: roles[0], password: '', mobile: '' });
  const [ownPassword, setOwnPassword] = useState(false);
  const m = useMessages();

  return (
    <div className="setup-two">
      <div>
        {m.view}
        {list.error && <LoadError error={list.error} retry={list.reload} />}
        {list.data && (
          <div className="table-wrap">
            <table className="grid compact">
              <thead><tr><th>{t('fleet.name')}</th><th>{t('login.email')}</th><th>{t('fleet.role')}</th><th>{t('fleet.lastLogin')}</th><th /></tr></thead>
              <tbody>
                {list.data.map(x => (
                  <tr key={x.userId} className={x.isActive ? undefined : 'inactive'}>
                    <td>{x.fullName}</td>
                    <td><bdi>{x.email}</bdi></td>
                    <td>{tx('role', x.role)}</td>
                    <td className="num">{x.lastLoginAt ? stamp(x.lastLoginAt, terminal.timeZone, lang) : '—'}</td>
                    <td className="cell-action">
                      {canEdit && x.userId !== user.userId && x.isActive && (
                        <ActionButton kind="secondary" onError={m.setError} onRun={async () => {
                          await api.post(`/api/users/${x.userId}/password-link`);
                          m.setDone(t('fleet.linkSent', { email: x.email }));
                        }}>{t('fleet.sendLink')}</ActionButton>
                      )}{' '}
                      {canEdit && x.userId !== user.userId && (
                        <ActionButton kind={x.isActive ? 'secondary' : 'primary'} onError={m.setError} onRun={async () => {
                          await api.post(`/api/users/${x.userId}/active`, { active: !x.isActive, reason: x.isActive ? 'Deactivated in the portal' : null });
                          list.reload();
                        }}>{x.isActive ? t('fleet.deactivate') : t('fleet.activate')}</ActionButton>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {canEdit && (
        <form className="book-side form-stack" onSubmit={e => e.preventDefault()}>
          <h2>{t('fleet.addUser')}</h2>
          <label className="field"><span>{t('fleet.name')}</span><input value={form.fullName} onChange={e => setForm({ ...form, fullName: e.target.value })} /></label>
          <label className="field"><span>{t('login.email')}</span><input type="email" dir="ltr" value={form.email} onChange={e => setForm({ ...form, email: e.target.value })} /></label>
          <label className="field"><span>{t('fleet.mobile')}</span><input dir="ltr" inputMode="tel" value={form.mobile} onChange={e => setForm({ ...form, mobile: e.target.value })} /></label>
          <label className="field"><span>{t('fleet.role')}</span>
            <select value={form.role} onChange={e => setForm({ ...form, role: e.target.value as UserRole })}>
              {roles.map(r => <option key={r} value={r}>{tx('role', r)}</option>)}
            </select></label>
          {ownPassword ? (
            <>
              <label className="field"><span>{t('fleet.firstPassword')}</span>
                <input type="text" dir="ltr" autoComplete="new-password" value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} /></label>
              <p className="hint">{t('fleet.passwordHint')}</p>
            </>
          ) : <p className="hint">{t('fleet.inviteHint')}</p>}
          <label className="radio"><input type="checkbox" checked={ownPassword} onChange={e => setOwnPassword(e.target.checked)} /> {t('fleet.ownPassword')}</label>
          <ActionButton kind="primary" disabled={!form.email.trim() || !form.fullName.trim() || (ownPassword && form.password.length < 10)} onError={m.setError}
            onRun={async () => {
              await api.post('/api/users', {
                email: form.email.trim(), fullName: form.fullName.trim(), role: form.role, password: ownPassword ? form.password : null,
                mobile: form.mobile.trim() || null, preferredLanguage: lang,
                companyId: staff ? null : companyId, terminalId: staff ? terminal.terminalId : null,
              });
              setForm({ email: '', fullName: '', role: roles[0], password: '', mobile: '' });
              m.setDone(ownPassword ? t('fleet.userAdded') : t('fleet.userInvited', { email: form.email.trim() }));
              list.reload();
            }}>{t('fleet.addUser')}</ActionButton>
        </form>
      )}
    </div>
  );
}

function Companies({ list, reload }: { list: CompanyRow[]; reload: () => void }) {
  const { api, t } = useApp();
  const [form, setForm] = useState({ name: '', crNumber: '', companyType: 'Trucking' });
  const m = useMessages();
  return (
    <div className="setup-two">
      <div>
        {m.view}
        <div className="table-wrap">
          <table className="grid compact">
            <thead><tr><th>{t('fleet.name')}</th><th>{t('fleet.cr')}</th><th>{t('fleet.type')}</th><th>{t('fleet.noShows')}</th><th>{t('appt.status')}</th><th /></tr></thead>
            <tbody>
              {list.map(c => (
                <tr key={c.companyId}>
                  <td>{c.name}</td>
                  <td className="num"><bdi>{c.crNumber ?? '—'}</bdi></td>
                  <td>{t(typeText[c.companyType as keyof typeof typeText] ?? 'fleet.type.Other')}</td>
                  <td className="num">{c.noShowCount}</td>
                  <td>{c.status === 'Active' ? <span className="ok-text">{t('fleet.active')}</span> : <span className="check-fail">{t(c.status === 'Suspended' ? 'fleet.suspended' : 'fleet.pending')}</span>}</td>
                  <td className="cell-action">
                    {c.status === 'Active'
                      ? <BlockButton blocked={false} label={t('fleet.suspend')} onError={m.setError}
                          onSet={async (_, reason) => { await api.post(`/api/companies/${c.companyId}/status`, { status: 'Suspended', reason }); reload(); }} />
                      : <ActionButton kind="primary" onError={m.setError}
                          onRun={async () => { await api.post(`/api/companies/${c.companyId}/status`, { status: 'Active', reason: 'Activated in the portal' }); reload(); }}>
                          {t('fleet.activate')}
                        </ActionButton>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
      <form className="book-side form-stack" onSubmit={e => e.preventDefault()}>
        <h2>{t('fleet.addCompany')}</h2>
        <label className="field"><span>{t('fleet.name')}</span><input value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} /></label>
        <label className="field"><span>{t('fleet.cr')}</span><input dir="ltr" value={form.crNumber} onChange={e => setForm({ ...form, crNumber: e.target.value })} /></label>
        <label className="field"><span>{t('fleet.type')}</span>
          <select value={form.companyType} onChange={e => setForm({ ...form, companyType: e.target.value })}>
            {Object.entries(typeText).map(([k, v]) => <option key={k} value={k}>{t(v)}</option>)}
          </select></label>
        <ActionButton kind="primary" disabled={!form.name.trim()} onError={m.setError} onRun={async () => {
          await api.post('/api/companies', { name: form.name.trim(), crNumber: form.crNumber.trim() || null, companyType: form.companyType });
          setForm({ name: '', crNumber: '', companyType: 'Trucking' });
          m.setDone(t('fleet.saved'));
          reload();
        }}>{t('fleet.addCompany')}</ActionButton>
      </form>
    </div>
  );
}

const typeText = {
  Trucking: 'fleet.type.Trucking',
  Forwarder: 'fleet.type.Forwarder',
  Consignee: 'fleet.type.Consignee',
  Line: 'fleet.type.Line',
  Other: 'fleet.type.Other',
} as const;

/**
 * Operators: groups of terminals with their own administrators. System admins set them up and move terminals between
 * them; an operator admin sees their own operator and adds fellow admins.
 */
function Operators() {
  const { api, t, lang, user, terminal } = useApp();
  const sys = user.role === 'SysAdmin';
  const list = useLoad(() => api.get<OperatorRow[]>('/api/operators'), []);
  const terminals = useLoad(() => (sys ? api.get<Terminal[]>('/api/terminals') : Promise.resolve([] as Terminal[])), [sys]);
  const [picked, setPicked] = useState<number | null>(null);
  const current = list.data?.find(o => o.operatorId === picked) ?? list.data?.[0] ?? null;
  const admins = useLoad(
    () => (current ? api.get<UserRow[]>(`/api/users?operatorId=${current.operatorId}`) : Promise.resolve([] as UserRow[])),
    [current?.operatorId]);
  const [created, setCreated] = useState({ code: '', name: '' });
  const [admin, setAdmin] = useState({ fullName: '', email: '' });
  const [adding, setAdding] = useState('');
  const m = useMessages();
  const free = (terminals.data ?? []).filter(x => !current?.terminals.some(y => y.terminalId === x.terminalId));

  return (
    <div className="setup-two">
      <div>
        {m.view}
        {list.error && <LoadError error={list.error} retry={list.reload} />}
        {list.data && list.data.length === 0 && <p className="muted">{t('fleet.noOperators')}</p>}
        {list.data && list.data.length > 0 && (
          <div className="table-wrap">
            <table className="grid compact">
              <thead><tr><th>{t('fleet.operatorCode')}</th><th>{t('fleet.name')}</th><th>{t('fleet.operatorTerminals')}</th><th className="num">{t('fleet.operatorAdmins')}</th><th /></tr></thead>
              <tbody>
                {list.data.map(o => (
                  <tr key={o.operatorId} className={[o.isActive ? '' : 'inactive', o.operatorId === current?.operatorId ? 'selected' : ''].join(' ').trim() || undefined}
                    onClick={() => setPicked(o.operatorId)}>
                    <td><bdi>{o.code}</bdi></td>
                    <td><button type="button" className="link-btn" aria-pressed={o.operatorId === current?.operatorId}
                      onClick={e => { e.stopPropagation(); setPicked(o.operatorId); }}>{o.name}</button></td>
                    <td><bdi>{o.terminals.map(x => x.code).join(', ') || '—'}</bdi></td>
                    <td className="num">{o.admins}</td>
                    <td className="cell-action" onClick={e => e.stopPropagation()}>
                      {sys && (
                        <ActionButton kind="secondary" onError={m.setError} onRun={async () => {
                          await api.put(`/api/operators/${o.operatorId}`, { name: o.name, isActive: !o.isActive });
                          list.reload();
                        }}>{o.isActive ? t('fleet.deactivate') : t('fleet.activate')}</ActionButton>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {current && (
          <div className="operator-detail">
            <h2>{current.name}</h2>
            <p className="hint">{t('fleet.operatorHint')}</p>
            <div className="table-wrap">
              <table className="grid compact">
                <thead><tr><th>{t('fleet.operatorTerminals')}</th><th /></tr></thead>
                <tbody>
                  {current.terminals.map(x => (
                    <tr key={x.terminalId}>
                      <td><strong><bdi>{x.code}</bdi></strong> {x.name}</td>
                      <td className="cell-action">
                        {sys && (
                          <ActionButton kind="secondary" onError={m.setError} onRun={async () => {
                            await api.put(`/api/terminals/${x.terminalId}/operator`, { operatorId: null });
                            list.reload();
                          }}>{t('fleet.takeOut')}</ActionButton>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {sys && free.length > 0 && (
              <div className="actions wrap">
                <label className="field field-inline">
                  <span>{t('fleet.addTerminal')}</span>
                  <select value={adding} onChange={e => setAdding(e.target.value)}>
                    <option value="">—</option>
                    {free.map(x => <option key={x.terminalId} value={x.terminalId}>{x.code} — {x.name}</option>)}
                  </select>
                </label>
                <ActionButton kind="primary" disabled={!adding} onError={m.setError} onRun={async () => {
                  await api.put(`/api/terminals/${adding}/operator`, { operatorId: current.operatorId });
                  setAdding('');
                  list.reload();
                }}>{t('fleet.addTerminal')}</ActionButton>
              </div>
            )}

            <h2>{t('fleet.operatorAdmins')}</h2>
            {admins.error && <LoadError error={admins.error} retry={admins.reload} />}
            {admins.data && (
              <div className="table-wrap">
                <table className="grid compact">
                  <thead><tr><th>{t('fleet.name')}</th><th>{t('login.email')}</th><th>{t('fleet.lastLogin')}</th><th /></tr></thead>
                  <tbody>
                    {admins.data.map(x => (
                      <tr key={x.userId} className={x.isActive ? undefined : 'inactive'}>
                        <td>{x.fullName}</td>
                        <td><bdi>{x.email}</bdi></td>
                        <td className="num">{x.lastLoginAt ? stamp(x.lastLoginAt, terminal.timeZone, lang) : '—'}</td>
                        <td className="cell-action">
                          {x.userId !== user.userId && (
                            <ActionButton kind={x.isActive ? 'secondary' : 'primary'} onError={m.setError} onRun={async () => {
                              await api.post(`/api/users/${x.userId}/active`, { active: !x.isActive, reason: x.isActive ? 'Deactivated in the portal' : null });
                              admins.reload();
                            }}>{x.isActive ? t('fleet.deactivate') : t('fleet.activate')}</ActionButton>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <BrandingForm key={current.operatorId} path={`/api/operators/${current.operatorId}/branding`}
              title={t('brand.operator')} hint={t('brand.operatorHint')} domain={sys ? 'edit' : 'show'} />
          </div>
        )}
      </div>

      <div className="form-stack">
        {current && (
          <form className="book-side form-stack" onSubmit={e => e.preventDefault()}>
            <h2>{t('fleet.addOperatorAdmin')}</h2>
            <p className="muted">{current.name}</p>
            <label className="field"><span>{t('fleet.name')}</span><input value={admin.fullName} onChange={e => setAdmin({ ...admin, fullName: e.target.value })} /></label>
            <label className="field"><span>{t('login.email')}</span><input type="email" dir="ltr" value={admin.email} onChange={e => setAdmin({ ...admin, email: e.target.value })} /></label>
            <p className="hint">{t('fleet.inviteHint')}</p>
            <ActionButton kind="primary" disabled={!admin.email.trim() || !admin.fullName.trim()} onError={m.setError} onRun={async () => {
              await api.post('/api/users', {
                email: admin.email.trim(), fullName: admin.fullName.trim(), role: 'OperatorAdmin', password: null,
                preferredLanguage: lang, companyId: null, terminalId: null, operatorId: current.operatorId,
              });
              m.setDone(t('fleet.userInvited', { email: admin.email.trim() }));
              setAdmin({ fullName: '', email: '' });
              admins.reload();
              list.reload();
            }}>{t('fleet.invite')}</ActionButton>
          </form>
        )}
        {sys && (
          <form className="book-side form-stack" onSubmit={e => e.preventDefault()}>
            <h2>{t('fleet.addOperator')}</h2>
            <label className="field"><span>{t('fleet.operatorCode')}</span>
              <input dir="ltr" maxLength={20} value={created.code} onChange={e => setCreated({ ...created, code: e.target.value.toUpperCase() })} /></label>
            <label className="field"><span>{t('fleet.name')}</span><input maxLength={150} value={created.name} onChange={e => setCreated({ ...created, name: e.target.value })} /></label>
            <ActionButton kind="primary" disabled={created.code.trim().length < 2 || !created.name.trim()} onError={m.setError} onRun={async () => {
              const result = await api.post<{ data: OperatorRow }>('/api/operators', { code: created.code.trim(), name: created.name.trim() });
              setCreated({ code: '', name: '' });
              setPicked(result.data.operatorId);
              list.reload();
            }}>{t('fleet.addOperator')}</ActionButton>
          </form>
        )}
        {sys && <BrandingForm path="/api/branding/installation" title={t('brand.installation')} hint={t('brand.installationHint')} domain="none" />}
      </div>
    </div>
  );
}

interface BrandSettingsRow {
  operatorId: number | null;
  name: string | null;
  color: string | null;
  domain: string | null;
  logoUrl: string | null;
}

/** Name, main colour, logo and (for operators) web address of the installation or an operator. */
function BrandingForm({ path, title, hint, domain }: { path: string; title: string; hint: string; domain: 'edit' | 'show' | 'none' }) {
  const { api, t } = useApp();
  const settings = useLoad(() => api.get<BrandSettingsRow>(path), [path]);
  const [form, setForm] = useState<{ name: string; color: string; domain: string } | null>(null);
  const m = useMessages();
  const data = settings.data;
  const values = form ?? (data ? { name: data.name ?? '', color: data.color ?? '', domain: data.domain ?? '' } : null);
  const color = values?.color && /^#[0-9a-f]{6}$/i.test(values.color) ? values.color.toLowerCase() : '';
  const ratio = color ? contrastWithWhite(color) : null;

  return (
    <form className="book-side form-stack brand-form" onSubmit={e => e.preventDefault()}>
      <h2>{title}</h2>
      <p className="hint">{hint}</p>
      {m.view}
      {settings.error && <LoadError error={settings.error} retry={settings.reload} />}
      {values && (
        <>
          <label className="field"><span>{t('brand.name')}</span>
            <input maxLength={80} value={values.name} placeholder={defaultBrand.name}
              onChange={e => setForm({ ...values, name: e.target.value })} /></label>
          <div className="field">
            <span id={`${path}-color`}>{t('brand.color')}</span>
            <div className="color-row">
              <input type="color" aria-labelledby={`${path}-color`} value={color || defaultBrand.color}
                onChange={e => setForm({ ...values, color: e.target.value })} />
              <input dir="ltr" maxLength={7} value={values.color} placeholder={defaultBrand.color} aria-labelledby={`${path}-color`}
                onChange={e => setForm({ ...values, color: e.target.value.trim() })} />
              <span className="color-sample" style={{ background: color || defaultBrand.color }}>{values.name || defaultBrand.name}</span>
            </div>
          </div>
          {ratio !== null && ratio < 4.5 && <p className="check-fail">{t('brand.tooLight', { ratio: ratio.toFixed(1) })}</p>}
          {domain !== 'none' && (
            <label className="field"><span>{t('brand.domain')}</span>
              <input dir="ltr" value={values.domain} disabled={domain === 'show'} placeholder="tas.example.com"
                onChange={e => setForm({ ...values, domain: e.target.value.trim() })} />
              <small className="hint">{t('brand.domainHint')}</small></label>
          )}
          <ActionButton kind="primary" disabled={ratio !== null && ratio < 4.5} onError={m.setError} onRun={async () => {
            await api.put(path, { name: values.name.trim() || null, color: values.color || null, domain: domain === 'none' ? null : values.domain || null });
            setForm(null);
            settings.reload();
            m.setDone(t('brand.saved'));
          }}>{t('brand.save')}</ActionButton>

          <div className="field">
            <span>{t('brand.logo')}</span>
            {data?.logoUrl ? <img className="brand-preview" src={data.logoUrl} alt={t('brand.logo')} /> : <p className="muted">{t('brand.noLogo')}</p>}
            <p className="hint">{t('brand.logoHint')}</p>
            <div className="actions wrap">
              <label className="btn btn-secondary file-btn">
                {t('brand.uploadLogo')}
                <input type="file" accept="image/png,image/jpeg,image/webp" className="visually-hidden" onChange={async e => {
                  const file = e.target.files?.[0];
                  e.target.value = '';
                  if (!file) return;
                  const body = new FormData();
                  body.append('file', file);
                  try {
                    await api.postForm(`${path}/logo`, body);
                    settings.reload();
                    m.setDone(t('brand.saved'));
                  } catch (err) {
                    m.setError(err instanceof Error ? err.message : String(err));
                  }
                }} />
              </label>
              {data?.logoUrl && (
                <ActionButton kind="secondary" onError={m.setError} onRun={async () => {
                  await api.del(`${path}/logo`);
                  settings.reload();
                }}>{t('brand.removeLogo')}</ActionButton>
              )}
            </div>
          </div>
        </>
      )}
    </form>
  );
}

interface ApiKeyRow {
  keyId: number;
  name: string;
  scope: 'Book' | 'Read';
  keyHint: string;
  createdAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
}

/** Keys for the company's own systems (TMS, dispatch) to use the API: each acts as a company user that can't sign in. */
function ApiKeys({ companyId }: { companyId: number }) {
  const { api, t, lang, terminal } = useApp();
  const base = `/api/companies/${companyId}/api-keys`;
  const list = useLoad(() => api.get<ApiKeyRow[]>(base), [base]);
  const [form, setForm] = useState<{ name: string; scope: 'Book' | 'Read' }>({ name: '', scope: 'Book' });
  const [issued, setIssued] = useState<{ name: string; key: string } | null>(null);
  const [asking, setAsking] = useState<number | null>(null);
  const m = useMessages();
  const scopeText = { Book: 'fleet.apiScope.Book', Read: 'fleet.apiScope.Read' } as const;

  return (
    <div className="setup-two">
      <div>
        {m.view}
        <p className="hint">{t('fleet.apiKeysHint')}</p>
        {list.error && <LoadError error={list.error} retry={list.reload} />}
        {list.data && list.data.length === 0 && <p className="muted">{t('fleet.apiKeysNone')}</p>}
        {list.data && list.data.length > 0 && (
          <div className="table-wrap">
            <table className="grid compact">
              <thead><tr><th>{t('fleet.name')}</th><th>{t('fleet.apiScope')}</th><th>{t('gatesys.key')}</th><th>{t('gatesys.lastUsed')}</th><th /></tr></thead>
              <tbody>
                {list.data.map(k => (
                  <tr key={k.keyId} className={k.revokedAt ? 'inactive' : undefined}>
                    <td dir="auto">{k.name}</td>
                    <td>{t(scopeText[k.scope])}</td>
                    <td className="nowrap"><bdi>{k.keyHint}</bdi></td>
                    <td className="num">{k.lastUsedAt ? stamp(k.lastUsedAt, terminal.timeZone, lang) : t('gatesys.never')}</td>
                    <td className="cell-action">
                      {k.revokedAt
                        ? <span className="muted">{t('gatesys.revoked', { when: stamp(k.revokedAt, terminal.timeZone, lang) })}</span>
                        : asking === k.keyId
                          ? (
                            <span className="inline-ask">
                              <ActionButton kind="danger" onError={m.setError} onRun={async () => {
                                await api.del(`${base}/${k.keyId}`);
                                setAsking(null);
                                list.reload();
                                m.setDone(t('gatesys.revokedDone', { name: k.name }));
                              }}>{t('gatesys.revokeYes', { name: k.name })}</ActionButton>
                              <button type="button" className="link-btn" onClick={() => setAsking(null)}>{t('lanes.keep')}</button>
                            </span>
                          )
                          : <button type="button" className="link-btn" onClick={() => setAsking(k.keyId)}>{t('gatesys.revoke')}</button>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {issued && (
          <div className="device-key" role="status">
            <p><strong>{t('gatesys.keyFor', { name: issued.name })}</strong> {t('fleet.apiKeyOnce')}</p>
            <code dir="ltr">{issued.key}</code>
            <div className="actions">
              <button type="button" className="btn btn-secondary" onClick={() => navigator.clipboard?.writeText(issued.key)}>{t('lanes.copy')}</button>
              <button type="button" className="link-btn" onClick={() => setIssued(null)}>{t('lanes.keyDone')}</button>
            </div>
          </div>
        )}
      </div>
      <form className="book-side form-stack" onSubmit={e => e.preventDefault()}>
        <h2>{t('fleet.addApiKey')}</h2>
        <label className="field"><span>{t('fleet.name')}</span>
          <input dir="auto" maxLength={60} value={form.name} placeholder={t('fleet.apiKeyPlaceholder')} onChange={e => setForm({ ...form, name: e.target.value })} /></label>
        <fieldset className="plain-fieldset">
          <legend>{t('fleet.apiScope')}</legend>
          {(['Book', 'Read'] as const).map(s => (
            <label key={s} className="radio">
              <input type="radio" name="api-scope" checked={form.scope === s} onChange={() => setForm({ ...form, scope: s })} /> {t(scopeText[s])}
            </label>
          ))}
        </fieldset>
        <ActionButton kind="primary" disabled={!form.name.trim()} onError={m.setError} onRun={async () => {
          const result = await api.post<{ data: { apiKey: ApiKeyRow; key: string } }>(base, { name: form.name.trim(), scope: form.scope });
          setIssued({ name: result.data.apiKey.name, key: result.data.key });
          setForm({ name: '', scope: 'Book' });
          list.reload();
        }}>{t('fleet.addApiKey')}</ActionButton>
      </form>
    </div>
  );
}
