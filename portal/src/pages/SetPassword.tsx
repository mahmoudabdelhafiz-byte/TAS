import { useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { PasswordLinkInfo } from '../api/types';
import { GateLayout } from '../components/GateLayout';
import { Field, Notice } from '../components/ui';
import { useI18n } from '../i18n/I18n';

/** The token arrives in the URL fragment (#token=…), which browsers never send to servers. */
export function readTokenFromHash(hash: string): string | null {
  const value = new URLSearchParams(hash.replace(/^#/, '')).get('token');
  return value && /^[A-Za-z0-9_-]{20,100}$/.test(value) ? value : null;
}

/** Both /reset-password and /accept-invite: the link itself says which it is. */
export function SetPasswordPage() {
  const { t } = useI18n();
  const navigate = useNavigate();
  const [token] = useState(() => readTokenFromHash(window.location.hash));
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [mismatch, setMismatch] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Keep the token out of the address bar (and browser history) once read.
  useEffect(() => {
    if (window.location.hash) window.history.replaceState(null, '', window.location.pathname);
  }, []);

  const link = useQuery({
    queryKey: ['password-link', token],
    queryFn: () => api<PasswordLinkInfo>('/api/auth/password-link', { body: { token }, anonymous: true }),
    enabled: token !== null,
    retry: false,
    staleTime: Infinity,
  });

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    if (password !== confirm) {
      setMismatch(true);
      return;
    }
    setMismatch(false);
    setBusy(true);
    try {
      await api('/api/auth/reset-password', { body: { token, newPassword: password }, anonymous: true });
      navigate('/sign-in', { replace: true, state: { noticeKey: 'passwordSaved' } });
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  if (token === null || link.isError) {
    return (
      <GateLayout>
        <header>
          <h1>{t('setPasswordTitle')}</h1>
        </header>
        <Notice kind="error">{t('linkInvalid')}</Notice>
        <div className="gate-links">
          <Link to="/forgot-password">{t('requestNewLink')}</Link>
          <Link to="/sign-in">{t('backToSignIn')}</Link>
        </div>
      </GateLayout>
    );
  }

  if (!link.data) {
    return (
      <GateLayout>
        <p className="muted" role="status">
          {t('checkingLink')}
        </p>
      </GateLayout>
    );
  }

  const invite = link.data.purpose === 'Invite';
  return (
    <GateLayout>
      <header>
        <h1>{invite ? t('acceptInviteTitle') : t('setPasswordTitle')}</h1>
        <p className="muted">
          {invite ? t('acceptInviteIntro', { name: link.data.fullName }) : t('setPasswordFor', { email: link.data.email })}
        </p>
      </header>
      <form onSubmit={submit} noValidate>
        {/* Lets password managers store the new password under the right account. */}
        <input type="email" name="username" autoComplete="username" value={link.data.email} readOnly hidden />
        <Field
          label={t('newPassword')}
          type="password"
          autoComplete="new-password"
          required
          minLength={10}
          hint={t('passwordRules')}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
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
        {error && <Notice kind="error">{error}</Notice>}
        <button type="submit" className="button" disabled={busy || !password || !confirm}>
          {busy ? t('savingPassword') : t('savePassword')}
        </button>
      </form>
    </GateLayout>
  );
}
