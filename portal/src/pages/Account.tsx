import { useState, type FormEvent } from 'react';
import { api, ApiError, changePassword } from '../api/client';
import type { UserProfile } from '../api/types';
import { useAuth, useUser } from '../auth/Auth';
import { Field, Notice } from '../components/ui';
import { useI18n } from '../i18n/I18n';
import { roleLabel } from '../layout/nav';

const emailLanguages: Record<string, string> = { ar: 'العربية', en: 'English', ur: 'اردو', hi: 'हिन्दी' };

export function AccountPage() {
  const { t } = useI18n();
  const user = useUser();

  return (
    <>
      <div className="page-head">
        <h1>{t('accountTitle')}</h1>
      </div>
      <section className="panel" aria-labelledby="profile-title">
        <div className="panel-body">
          <h2 id="profile-title">{t('profile')}</h2>
          <dl style={{ display: 'grid', gridTemplateColumns: 'max-content 1fr', gap: '0.4rem 1.5rem', margin: 0 }}>
            <dt className="muted">{t('fullName')}</dt>
            <dd style={{ margin: 0 }}>{user.fullName}</dd>
            <dt className="muted">{t('email')}</dt>
            <dd style={{ margin: 0 }} className="code">
              {user.email}
            </dd>
            <dt className="muted">{t('role')}</dt>
            <dd style={{ margin: 0 }}>
              {t(roleLabel(user.role))}
              {user.companyName ? ` — ${user.companyName}` : ''}
            </dd>
          </dl>
          <EmailLanguageForm />
        </div>
      </section>
      <ChangePasswordForm />
    </>
  );
}

/** The language of emails and SMS (any of the four), separate from the portal language toggle. */
function EmailLanguageForm() {
  const { t } = useI18n();
  const { setUser } = useAuth();
  const user = useUser();
  const [value, setValue] = useState(user.preferredLanguage);
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      setUser(await api<UserProfile>('/api/auth/me/language', { method: 'PUT', body: { language: value } }));
      setMessage({ kind: 'success', text: t('languageSaved', { language: emailLanguages[value] ?? value }) });
    } catch (err) {
      setMessage({ kind: 'error', text: (err as Error).message });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="form-actions" style={{ alignItems: 'end' }}>
      <div className="field">
        <label htmlFor="email-language">{t('emailLanguage')}</label>
        <select id="email-language" value={value} onChange={(e) => setValue(e.target.value)}>
          {Object.entries(emailLanguages).map(([code, name]) => (
            <option key={code} value={code} lang={code}>
              {name}
            </option>
          ))}
        </select>
      </div>
      <button type="submit" className="button secondary" disabled={busy || value === user.preferredLanguage}>
        {t('saveLanguage')}
      </button>
      {message && <Notice kind={message.kind}>{message.text}</Notice>}
    </form>
  );
}

function ChangePasswordForm() {
  const { t } = useI18n();
  const { setUser } = useAuth();
  const user = useUser();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [mismatch, setMismatch] = useState(false);
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setMessage(null);
    if (next !== confirm) {
      setMismatch(true);
      return;
    }
    setMismatch(false);
    setBusy(true);
    try {
      setUser(await changePassword(user.email, current, next));
      setCurrent('');
      setNext('');
      setConfirm('');
      setMessage({ kind: 'success', text: t('passwordChanged') });
    } catch (err) {
      const text = err instanceof ApiError && err.status === 403 ? t('wrongCurrentPassword') : (err as Error).message;
      setMessage({ kind: 'error', text });
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="panel" aria-labelledby="password-title">
      <form className="panel-body" onSubmit={submit} noValidate style={{ maxInlineSize: '28rem' }}>
        <h2 id="password-title">{t('changePassword')}</h2>
        <input type="email" name="username" autoComplete="username" value={user.email} readOnly hidden />
        <Field label={t('currentPassword')} type="password" autoComplete="current-password" required value={current} onChange={(e) => setCurrent(e.target.value)} />
        <Field
          label={t('newPassword')}
          type="password"
          autoComplete="new-password"
          required
          hint={t('passwordRules')}
          value={next}
          onChange={(e) => setNext(e.target.value)}
        />
        <Field
          label={t('confirmPassword')}
          type="password"
          autoComplete="new-password"
          required
          error={mismatch ? t('passwordsDiffer') : null}
          value={confirm}
          onChange={(e) => setConfirm(e.target.value)}
        />
        {message && <Notice kind={message.kind}>{message.text}</Notice>}
        <div className="form-actions">
          <button type="submit" className="button" disabled={busy || !current || !next || !confirm}>
            {busy ? t('savingPassword') : t('changePassword')}
          </button>
        </div>
      </form>
    </section>
  );
}
