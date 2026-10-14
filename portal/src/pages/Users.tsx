import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Navigate } from 'react-router';
import { api } from '../api/client';
import { companyRoles, type Company, type UserRole, type UserView } from '../api/types';
import { canManageUsers, invitableRoles, isCompanyRole, useUser } from '../auth/Auth';
import { Field, LoadError, Notice } from '../components/ui';
import { useI18n, type Language } from '../i18n/I18n';
import type { MessageKey } from '../i18n/en';
import { roleLabel } from '../layout/nav';
import { useTerminal } from '../terminals/Terminals';

const emailLanguages: { code: string; name: string }[] = [
  { code: 'ar', name: 'العربية' },
  { code: 'en', name: 'English' },
  { code: 'ur', name: 'اردو' },
  { code: 'hi', name: 'हिन्दी' },
];

type Flash = { kind: 'success' | 'error'; text: string } | null;

export function userState(user: UserView, now: Date = new Date()): { key: MessageKey; tag: string; until?: string } {
  if (!user.isActive) return { key: 'stateDeactivated', tag: 'off' };
  if (!user.passwordSet) return { key: 'stateInvited', tag: 'pending' };
  if (user.lockedUntil && new Date(user.lockedUntil) > now) return { key: 'stateLocked', tag: 'off', until: user.lockedUntil };
  return { key: 'stateActive', tag: '' };
}

export function UsersPage() {
  const { t, language, formatDateTime } = useI18n();
  const user = useUser();
  const terminal = useTerminal();
  const queries = useQueryClient();
  const [inviting, setInviting] = useState(false);
  const [flash, setFlash] = useState<Flash>(null);
  const [deactivating, setDeactivating] = useState<number | null>(null);
  const [reason, setReason] = useState('');

  const users = useQuery({ queryKey: ['users'], queryFn: () => api<UserView[]>('/api/users') });
  const companies = useQuery({ queryKey: ['companies'], queryFn: () => api<Company[]>('/api/companies') });
  const companyName = (id: number | null) => companies.data?.find((c) => c.companyId === id)?.name;

  const refresh = () => queries.invalidateQueries({ queryKey: ['users'] });
  const fail = (err: unknown) => setFlash({ kind: 'error', text: (err as Error).message });

  const resend = useMutation({
    mutationFn: (u: UserView) => api(`/api/users/${u.userId}/invite`, { method: 'POST' }).then(() => u),
    onSuccess: (u) => setFlash({ kind: 'success', text: t('invitationResent', { email: u.email }) }),
    onError: fail,
  });

  const setActive = useMutation({
    mutationFn: ({ u, active, why }: { u: UserView; active: boolean; why?: string }) =>
      api(`/api/users/${u.userId}/active`, { body: { active, reason: why } }).then(() => ({ u, active })),
    onSuccess: ({ u, active }) => {
      setDeactivating(null);
      setReason('');
      setFlash({ kind: 'success', text: t(active ? 'reactivated' : 'deactivated', { name: u.fullName }) });
      void refresh();
    },
    onError: fail,
  });

  if (!canManageUsers(user.role)) return <Navigate to="/appointments" replace />;

  const list = [...(users.data ?? [])].sort((a, b) => a.fullName.localeCompare(b.fullName, language));
  const lastSeen = (iso: string | null) =>
    iso ? formatDateTime(iso, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }, terminal.timeZone) : t('never');

  return (
    <>
      <div className="page-head">
        <h1>{t('usersTitle')}</h1>
        {!inviting && (
          <button type="button" className="button" onClick={() => (setInviting(true), setFlash(null))}>
            {t('inviteUser')}
          </button>
        )}
      </div>

      {flash && <Notice kind={flash.kind}>{flash.text}</Notice>}

      {inviting && (
        <InviteForm
          companies={companies.data ?? []}
          language={language}
          onCancel={() => setInviting(false)}
          onSent={(email) => {
            setInviting(false);
            setFlash({ kind: 'success', text: t('invitationSent', { email }) });
            void refresh();
          }}
        />
      )}

      {users.isError ? (
        <LoadError error={users.error} onRetry={() => void users.refetch()} />
      ) : users.isLoading ? (
        <p className="muted" role="status">
          {t('loading')}
        </p>
      ) : list.length === 0 ? (
        <div className="panel empty">
          <p>{t('noUsers')}</p>
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th scope="col">{t('colUser')}</th>
                <th scope="col">{t('colRole')}</th>
                <th scope="col">{t('colState')}</th>
                <th scope="col">{t('colLastSignIn')}</th>
                <th scope="col">
                  <span className="visually-hidden">{t('colState')}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {list.map((u) => {
                const state = userState(u);
                const self = u.userId === user.userId;
                return (
                  <tr key={u.userId}>
                    <td>
                      {u.fullName} {self && <span className="tag">{t('you')}</span>}
                      <span className="sub code">{u.email}</span>
                    </td>
                    <td>
                      {t(roleLabel(u.role))}
                      {u.companyId !== null && <span className="sub">{companyName(u.companyId)}</span>}
                    </td>
                    <td>
                      <span className={`tag ${state.tag}`}>
                        {t(state.key, { time: state.until ? lastSeen(state.until) : '' })}
                      </span>
                    </td>
                    <td className="muted">{lastSeen(u.lastLoginAt)}</td>
                    <td>
                      {!self && deactivating === u.userId ? (
                        <form
                          className="actions"
                          onSubmit={(e) => {
                            e.preventDefault();
                            setActive.mutate({ u, active: false, why: reason.trim() });
                          }}
                        >
                          <Field label={t('deactivateReason', { name: u.fullName })} value={reason} onChange={(e) => setReason(e.target.value)} required autoFocus />
                          <div className="form-actions">
                            <button type="submit" className="button" disabled={!reason.trim() || setActive.isPending}>
                              {t('deactivate')}
                            </button>
                            <button type="button" className="button secondary" onClick={() => setDeactivating(null)}>
                              {t('cancel')}
                            </button>
                          </div>
                        </form>
                      ) : (
                        !self && (
                          <div className="actions">
                            {u.isActive && !u.passwordSet && (
                              <button type="button" className="button quiet" disabled={resend.isPending} onClick={() => resend.mutate(u)}>
                                {t('resendInvitation')}
                              </button>
                            )}
                            {u.isActive ? (
                              <button type="button" className="button quiet danger" onClick={() => (setDeactivating(u.userId), setReason(''))}>
                                {t('deactivate')}
                              </button>
                            ) : (
                              <button type="button" className="button quiet" onClick={() => setActive.mutate({ u, active: true })}>
                                {t('reactivate')}
                              </button>
                            )}
                          </div>
                        )
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

function InviteForm({
  companies,
  language,
  onCancel,
  onSent,
}: {
  companies: Company[];
  language: Language;
  onCancel: () => void;
  onSent: (email: string) => void;
}) {
  const { t } = useI18n();
  const user = useUser();
  const terminal = useTerminal();
  const roles = invitableRoles(user.role);
  const [fullName, setFullName] = useState('');
  const [email, setEmail] = useState('');
  const [mobile, setMobile] = useState('');
  const [role, setRole] = useState<UserRole>(roles[0] ?? 'CompanyUser');
  const [companyId, setCompanyId] = useState<number | ''>(user.companyId ?? '');
  const [emailLanguage, setEmailLanguage] = useState<string>(language);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const needsCompany = companyRoles.includes(role) && !isCompanyRole(user.role);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const forCompany = companyRoles.includes(role);
      await api('/api/users', {
        body: {
          email: email.trim(),
          fullName: fullName.trim(),
          role,
          companyId: forCompany ? (isCompanyRole(user.role) ? user.companyId : companyId || null) : null,
          terminalId: forCompany ? null : (user.terminalId ?? terminal.terminalId),
          mobile: mobile.trim() || null,
          preferredLanguage: emailLanguage,
        },
      });
      onSent(email.trim());
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  return (
    <section className="panel" aria-labelledby="invite-title">
      <form className="panel-body" onSubmit={submit}>
        <div>
          <h2 id="invite-title">{t('inviteUser')}</h2>
          <p className="muted">{t('inviteIntro')}</p>
        </div>
        <div className="form-grid">
          <Field label={t('fullName')} required value={fullName} onChange={(e) => setFullName(e.target.value)} autoComplete="off" />
          <Field label={t('email')} type="email" required value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="off" />
          <div className="field">
            <label htmlFor="invite-role">{t('role')}</label>
            <select id="invite-role" value={role} onChange={(e) => setRole(e.target.value as UserRole)}>
              {roles.map((r) => (
                <option key={r} value={r}>
                  {t(roleLabel(r))}
                </option>
              ))}
            </select>
          </div>
          {needsCompany && (
            <div className="field">
              <label htmlFor="invite-company">{t('company')}</label>
              <select id="invite-company" required value={companyId} onChange={(e) => setCompanyId(e.target.value ? Number(e.target.value) : '')}>
                <option value="" />
                {companies.map((c) => (
                  <option key={c.companyId} value={c.companyId}>
                    {c.name}
                  </option>
                ))}
              </select>
            </div>
          )}
          <Field label={t('mobile')} type="tel" value={mobile} onChange={(e) => setMobile(e.target.value)} placeholder="+9665…" />
          <div className="field">
            <label htmlFor="invite-language">{t('emailLanguage')}</label>
            <select id="invite-language" value={emailLanguage} onChange={(e) => setEmailLanguage(e.target.value)}>
              {emailLanguages.map((l) => (
                <option key={l.code} value={l.code} lang={l.code}>
                  {l.name}
                </option>
              ))}
            </select>
          </div>
        </div>
        {error && <Notice kind="error">{error}</Notice>}
        <div className="form-actions">
          <button type="submit" className="button" disabled={busy || !fullName.trim() || !email.trim() || (needsCompany && !companyId)}>
            {busy ? t('sendingInvitation') : t('sendInvitation')}
          </button>
          <button type="button" className="button secondary" onClick={onCancel}>
            {t('cancel')}
          </button>
        </div>
      </form>
    </section>
  );
}
