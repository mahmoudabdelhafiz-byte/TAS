import { useState, type FormEvent } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import type { AdminResponse, Company, Driver, Truck, UserRole } from '../api/types';
import { isCompanyRole, useUser } from '../auth/Auth';
import { Field, LoadError, Notice } from '../components/ui';
import { useI18n } from '../i18n/I18n';

/** Same rules as the API (AccessRules.CanManageFleet / CanBlockFleet). */
export const canEditFleet = (role: UserRole) => role === 'CompanyAdmin' || role === 'TerminalAdmin' || role === 'SysAdmin';
export const canBlockFleet = (role: UserRole) => role === 'TerminalAdmin' || role === 'SysAdmin';

const smsLanguages: { code: string; name: string }[] = [
  { code: 'ar', name: 'العربية' },
  { code: 'en', name: 'English' },
  { code: 'ur', name: 'اردو' },
  { code: 'hi', name: 'हिन्दी' },
];

type Flash = { kind: 'success' | 'error'; text: string } | null;

export function FleetPage() {
  const { t } = useI18n();
  const user = useUser();
  const staff = !isCompanyRole(user.role);
  const [companyId, setCompanyId] = useState<number | null>(user.companyId);
  const [flash, setFlash] = useState<Flash>(null);
  const companies = useQuery({ queryKey: ['companies'], queryFn: () => api<Company[]>('/api/companies'), enabled: staff });

  return (
    <>
      <div className="page-head">
        <h1>{t('fleetTitle')}</h1>
        {staff && (
          <div className="field" style={{ minInlineSize: '16rem' }}>
            <label htmlFor="fleet-company">{t('company')}</label>
            <select id="fleet-company" value={companyId ?? ''} onChange={(e) => (setCompanyId(e.target.value ? Number(e.target.value) : null), setFlash(null))}>
              <option value="">{t('chooseCompany')}</option>
              {(companies.data ?? []).map((c) => (
                <option key={c.companyId} value={c.companyId}>
                  {c.name}
                </option>
              ))}
            </select>
          </div>
        )}
      </div>
      {!canEditFleet(user.role) && <p className="muted">{t('fleetReadOnly')}</p>}
      {flash && <Notice kind={flash.kind}>{flash.text}</Notice>}
      {companyId !== null && (
        <div className="detail-grid" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 24rem), 1fr))' }}>
          <TrucksPanel companyId={companyId} onFlash={setFlash} />
          <DriversPanel companyId={companyId} onFlash={setFlash} />
        </div>
      )}
    </>
  );
}

interface PanelProps {
  companyId: number;
  onFlash: (flash: Flash) => void;
}

/** Block / unblock with a reason, shown inline in the row (terminal admins). */
function BlockControl({ name, blocked, onSet }: { name: string; blocked: boolean; onSet: (blocked: boolean, reason: string) => Promise<void> }) {
  const { t } = useI18n();
  const [asking, setAsking] = useState(false);
  const [reason, setReason] = useState('');
  if (blocked) {
    return (
      <button type="button" className="button quiet" onClick={() => void onSet(false, '')}>
        {t('unblock')}
      </button>
    );
  }
  if (!asking) {
    return (
      <button type="button" className="button quiet danger" onClick={() => setAsking(true)}>
        {t('block')}
      </button>
    );
  }
  return (
    <form
      className="actions"
      onSubmit={(e) => {
        e.preventDefault();
        void onSet(true, reason.trim()).then(() => setAsking(false));
      }}
    >
      <Field label={`${t('blockReason')} (${name})`} value={reason} onChange={(e) => setReason(e.target.value)} required autoFocus />
      <div className="form-actions">
        <button type="submit" className="button" disabled={!reason.trim()}>
          {t('block')}
        </button>
        <button type="button" className="button secondary" onClick={() => setAsking(false)}>
          {t('cancel')}
        </button>
      </div>
    </form>
  );
}

function TrucksPanel({ companyId, onFlash }: PanelProps) {
  const { t } = useI18n();
  const user = useUser();
  const queries = useQueryClient();
  const trucks = useQuery({ queryKey: ['trucks', companyId], queryFn: () => api<Truck[]>(`/api/companies/${companyId}/trucks`) });
  const [editing, setEditing] = useState<number | 'new' | null>(null);
  const [plate, setPlate] = useState('');
  const [busy, setBusy] = useState(false);
  const refresh = () => queries.invalidateQueries({ queryKey: ['trucks', companyId] });

  async function save(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    try {
      const saved =
        editing === 'new'
          ? await api<AdminResponse<Truck>>(`/api/companies/${companyId}/trucks`, { body: { plateNumber: plate } })
          : await api<AdminResponse<Truck>>(`/api/companies/${companyId}/trucks/${editing}`, { method: 'PUT', body: { plateNumber: plate } });
      onFlash({ kind: 'success', text: t(editing === 'new' ? 'truckAdded' : 'truckSaved', { plate: saved.data.plateNumber }) });
      setEditing(null);
      setPlate('');
      await refresh();
    } catch (err) {
      onFlash({ kind: 'error', text: (err as Error).message });
    } finally {
      setBusy(false);
    }
  }

  async function setBlocked(truck: Truck, blocked: boolean, reason: string) {
    try {
      await api(`/api/companies/${companyId}/trucks/${truck.truckId}/block`, { body: { blocked, reason: reason || null } });
      onFlash({ kind: 'success', text: t(blocked ? 'blockedNotice' : 'unblockedNotice', { name: truck.plateNumber }) });
      await refresh();
    } catch (err) {
      onFlash({ kind: 'error', text: (err as Error).message });
    }
  }

  const form = (
    <form className="form-actions" style={{ alignItems: 'end' }} onSubmit={save}>
      <Field label={t('plate')} hint={t('plateHint')} value={plate} onChange={(e) => setPlate(e.target.value)} required autoFocus />
      <button type="submit" className="button" disabled={busy || !plate.trim()}>
        {busy ? t('saving') : t('save')}
      </button>
      <button type="button" className="button secondary" onClick={() => setEditing(null)}>
        {t('cancel')}
      </button>
    </form>
  );

  return (
    <section className="panel" aria-labelledby="trucks-title">
      <div className="panel-body">
        <div className="page-head">
          <h2 id="trucks-title">{t('trucks')}</h2>
          {canEditFleet(user.role) && editing === null && (
            <button type="button" className="button secondary" onClick={() => (setEditing('new'), setPlate(''))}>
              {t('addTruck')}
            </button>
          )}
        </div>
        {editing === 'new' && form}
        {trucks.isError ? (
          <LoadError error={trucks.error} onRetry={() => void trucks.refetch()} />
        ) : !trucks.data ? (
          <p className="muted">{t('loading')}</p>
        ) : trucks.data.length === 0 ? (
          <p className="muted">{t('noTrucks')}</p>
        ) : (
          <ul className="fleet-list">
            {trucks.data.map((truck) => (
              <li key={truck.truckId}>
                {editing === truck.truckId ? (
                  form
                ) : (
                  <>
                    <span>
                      <span className="code" style={{ fontWeight: 600 }}>
                        {truck.plateNumber}
                      </span>{' '}
                      {truck.isBlocked && <span className="tag off">{t('blocked')}</span>}
                    </span>
                    <span className="actions">
                      {canEditFleet(user.role) && editing === null && (
                        <button type="button" className="button quiet" onClick={() => (setEditing(truck.truckId), setPlate(truck.plateNumber))}>
                          {t('edit')}
                        </button>
                      )}
                      {canBlockFleet(user.role) && <BlockControl name={truck.plateNumber} blocked={truck.isBlocked} onSet={(b, r) => setBlocked(truck, b, r)} />}
                    </span>
                  </>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}

interface DriverDraft {
  fullName: string;
  idNumber: string;
  mobile: string;
  preferredLanguage: string;
}

const emptyDriver: DriverDraft = { fullName: '', idNumber: '', mobile: '', preferredLanguage: 'ar' };

function DriversPanel({ companyId, onFlash }: PanelProps) {
  const { t } = useI18n();
  const user = useUser();
  const queries = useQueryClient();
  const drivers = useQuery({ queryKey: ['drivers', companyId], queryFn: () => api<Driver[]>(`/api/companies/${companyId}/drivers`) });
  const [editing, setEditing] = useState<number | 'new' | null>(null);
  const [draft, setDraft] = useState<DriverDraft>(emptyDriver);
  const [busy, setBusy] = useState(false);
  const refresh = () => queries.invalidateQueries({ queryKey: ['drivers', companyId] });
  const change = (part: Partial<DriverDraft>) => setDraft((d) => ({ ...d, ...part }));

  async function save(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    try {
      const saved =
        editing === 'new'
          ? await api<AdminResponse<Driver>>(`/api/companies/${companyId}/drivers`, { body: draft })
          : await api<AdminResponse<Driver>>(`/api/companies/${companyId}/drivers/${editing}`, { method: 'PUT', body: draft });
      onFlash({ kind: 'success', text: t(editing === 'new' ? 'driverAdded' : 'driverSaved', { name: saved.data.fullName }) });
      setEditing(null);
      setDraft(emptyDriver);
      await refresh();
    } catch (err) {
      onFlash({ kind: 'error', text: (err as Error).message });
    } finally {
      setBusy(false);
    }
  }

  async function setBlocked(driver: Driver, blocked: boolean, reason: string) {
    try {
      await api(`/api/companies/${companyId}/drivers/${driver.driverId}/block`, { body: { blocked, reason: reason || null } });
      onFlash({ kind: 'success', text: t(blocked ? 'blockedNotice' : 'unblockedNotice', { name: driver.fullName }) });
      await refresh();
    } catch (err) {
      onFlash({ kind: 'error', text: (err as Error).message });
    }
  }

  const form = (
    <form className="panel-body" style={{ padding: 0 }} onSubmit={save}>
      <div className="form-grid">
        <Field label={t('fullName')} value={draft.fullName} onChange={(e) => change({ fullName: e.target.value })} required autoFocus />
        <Field label={t('idNumber')} value={draft.idNumber} onChange={(e) => change({ idNumber: e.target.value })} required dir="ltr" inputMode="numeric" />
        <Field label={t('driverMobile')} type="tel" value={draft.mobile} onChange={(e) => change({ mobile: e.target.value })} required placeholder="05…" />
        <div className="field">
          <label htmlFor="driver-language">{t('driverLanguage')}</label>
          <select id="driver-language" value={draft.preferredLanguage} onChange={(e) => change({ preferredLanguage: e.target.value })}>
            {smsLanguages.map((l) => (
              <option key={l.code} value={l.code} lang={l.code}>
                {l.name}
              </option>
            ))}
          </select>
        </div>
      </div>
      <div className="form-actions">
        <button type="submit" className="button" disabled={busy || !draft.fullName.trim() || !draft.idNumber.trim() || !draft.mobile.trim()}>
          {busy ? t('saving') : t('save')}
        </button>
        <button type="button" className="button secondary" onClick={() => setEditing(null)}>
          {t('cancel')}
        </button>
      </div>
    </form>
  );

  return (
    <section className="panel" aria-labelledby="drivers-title">
      <div className="panel-body">
        <div className="page-head">
          <h2 id="drivers-title">{t('drivers')}</h2>
          {canEditFleet(user.role) && editing === null && (
            <button type="button" className="button secondary" onClick={() => (setEditing('new'), setDraft(emptyDriver))}>
              {t('addDriver')}
            </button>
          )}
        </div>
        {editing === 'new' && form}
        {drivers.isError ? (
          <LoadError error={drivers.error} onRetry={() => void drivers.refetch()} />
        ) : !drivers.data ? (
          <p className="muted">{t('loading')}</p>
        ) : drivers.data.length === 0 ? (
          <p className="muted">{t('noDrivers')}</p>
        ) : (
          <ul className="fleet-list">
            {drivers.data.map((driver) => (
              <li key={driver.driverId}>
                {editing === driver.driverId ? (
                  form
                ) : (
                  <>
                    <span>
                      {driver.fullName} {driver.isBlocked && <span className="tag off">{t('blocked')}</span>}
                      <span className="sub code">{driver.idNumber}</span>
                      <span className="sub code">{driver.mobile}</span>
                    </span>
                    <span className="actions">
                      {canEditFleet(user.role) && editing === null && (
                        <button
                          type="button"
                          className="button quiet"
                          onClick={() => (
                            setEditing(driver.driverId),
                            setDraft({ fullName: driver.fullName, idNumber: driver.idNumber, mobile: driver.mobile, preferredLanguage: driver.preferredLanguage })
                          )}
                        >
                          {t('edit')}
                        </button>
                      )}
                      {canBlockFleet(user.role) && <BlockControl name={driver.fullName} blocked={driver.isBlocked} onSet={(b, r) => setBlocked(driver, b, r)} />}
                    </span>
                  </>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}
