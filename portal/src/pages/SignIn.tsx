import { useState, type FormEvent } from 'react';
import { Link, useLocation, useNavigate } from 'react-router';
import { ApiError } from '../api/client';
import { useAuth } from '../auth/Auth';
import { GateLayout } from '../components/GateLayout';
import { Field, Notice } from '../components/ui';
import { useI18n } from '../i18n/I18n';

export function SignInPage() {
  const { t } = useI18n();
  const { signIn, state } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const from = (location.state as { from?: string } | null)?.from ?? '/';
  const notice = (location.state as { notice?: string } | null)?.notice;

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await signIn(email.trim(), password);
      navigate(from, { replace: true });
    } catch (err) {
      const status = err instanceof ApiError ? err.status : 0;
      setError(status === 423 ? t('accountLocked') : status === 429 ? t('tooManyRequests') : status === 401 ? t('wrongCredentials') : (err as Error).message);
      setBusy(false);
    }
  }

  return (
    <GateLayout>
      <header>
        <h1>{t('signInTitle')}</h1>
        <p className="muted">{t('signInIntro')}</p>
      </header>
      {state.status === 'signed-out' && state.reason === 'session-ended' && <Notice>{t('sessionEnded')}</Notice>}
      {notice && <Notice kind="success">{notice}</Notice>}
      <form onSubmit={submit} noValidate>
        <Field label={t('email')} type="email" name="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
        <Field
          label={t('password')}
          type="password"
          name="password"
          autoComplete="current-password"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
        {error && <Notice kind="error">{error}</Notice>}
        <button type="submit" className="button" disabled={busy}>
          {busy ? t('signingIn') : t('signIn')}
        </button>
        <div className="gate-links">
          <Link to="/forgot-password" state={{ email }}>
            {t('forgotPasswordLink')}
          </Link>
        </div>
      </form>
    </GateLayout>
  );
}
