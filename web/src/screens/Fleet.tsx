import { useState } from 'react';
import { isCompanyRole } from '../lib/routes.ts';
import { stamp } from '../lib/time.ts';
import type { Company, Driver, Truck, UserRole } from '../lib/types.ts';
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

type Tab = 'trucks' | 'drivers' | 'users' | 'companies';
const tabText = { trucks: 'fleet.trucks', drivers: 'fleet.drivers', users: 'fleet.users', companies: 'fleet.companies' } as const;

/** Trucks, drivers and users of a company; companies for terminal admins. */
export function Fleet() {
  const { api, t, user, route, go } = useApp();
  const companyUser = isCompanyRole(user.role);
  const terminalAdmin = user.role === 'TerminalAdmin' || user.role === 'SysAdmin';
  const tabs: Tab[] = terminalAdmin ? ['companies', 'trucks', 'drivers', 'users']
    : user.role === 'CompanyAdmin' ? ['trucks', 'drivers', 'users'] : ['trucks', 'drivers'];
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
        {!companyUser && tab !== 'companies' && (
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
      {tab === 'trucks' && companyId && companyId > 0 && <Trucks companyId={companyId} canEdit={canEdit} canBlock={terminalAdmin} />}
      {tab === 'drivers' && companyId && companyId > 0 && <Drivers companyId={companyId} canEdit={canEdit} canBlock={terminalAdmin} />}
      {tab === 'users' && companyId && <Users companyId={companyId} canEdit={canEdit} />}
      {tab !== 'companies' && companyId && companyId < 0 && tab !== 'users' && <p className="muted">{t('common.chooseCompany')}</p>}
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

const languages = [['ar', 'العربية'], ['en', 'English'], ['ur', 'اردو'], ['hi', 'हिन्दी']] as const;

function Drivers({ companyId, canEdit, canBlock }: { companyId: number; canEdit: boolean; canBlock: boolean }) {
  const { api, t } = useApp();
  const base = `/api/companies/${companyId}/drivers`;
  const list = useLoad(() => api.get<(Driver & { idNumber: string })[]>(base), [base]);
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
                    <td>{languages.find(l => l[0] === x.preferredLanguage)?.[1] ?? x.preferredLanguage}</td>
                    <td>{x.isBlocked ? <span className="check-fail">{t('fleet.blocked')}</span> : <span className="ok-text">{t('fleet.active')}</span>}</td>
                    <td className="cell-action">
                      {canBlock && <BlockButton blocked={x.isBlocked} onError={m.setError}
                        onSet={async (blocked, reason) => { await api.post(`${base}/${x.driverId}/block`, { blocked, reason }); list.reload(); }} />}
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
          <label className="field"><span>{t('fleet.idNumber')}</span>
            <input value={form.idNumber} dir="ltr" onChange={e => setForm({ ...form, idNumber: e.target.value })} /></label>
          <label className="field"><span>{t('fleet.mobile')}</span>
            <input value={form.mobile} dir="ltr" inputMode="tel" placeholder="05XXXXXXXX" onChange={e => setForm({ ...form, mobile: e.target.value })} /></label>
          <label className="field"><span>{t('fleet.messagesIn')}</span>
            <select value={form.preferredLanguage} onChange={e => setForm({ ...form, preferredLanguage: e.target.value })}>
              {languages.map(([code, name]) => <option key={code} value={code}>{name}</option>)}
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
