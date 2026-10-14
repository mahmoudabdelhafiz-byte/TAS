import { useState, type FormEvent } from 'react';
import { Link, useLocation } from 'react-router';
import { api, ApiError } from '../api/client';
import { GateLayout } from '../components/GateLayout';
import { Field, Notice } from '../components/ui';
import { useI18n } from '../i18n/I18n';

export function ForgotPasswordPage() {
  const { t } = useI18n();
  const location = useLocation();
  const [email, setEmail] = useState((location.state as { email?: string } | null)?.email ?? '');
  const [sentTo, setSentTo] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await api('/api/auth/forgot-password', { body: { email: email.trim() }, anonymous: true });
      setSentTo(email.trim());
    } catch (err) {
      setError(err instanceof ApiError && err.status === 429 ? t('tooManyRequests') : (err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <GateLayout>
      {sentTo ? (
        <>
          <header>
            <h1>{t('linkSentTitle')}</h1>
          </header>
          <Notice kind="success">{t('linkSent', { email: sentTo })}</Notice>
          <Link to="/sign-in">{t('backToSignIn')}</Link>
        </>
      ) : (
        <>
          <header>
            <h1>{t('forgotTitle')}</h1>
            <p className="muted">{t('forgotIntro')}</p>
          </header>
          <form onSubmit={submit} noValidate>
            <Field label={t('email')} type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
            {error && <Notice kind="error">{error}</Notice>}
            <button type="submit" className="button" disabled={busy || !email.trim()}>
              {busy ? t('sendingLink') : t('sendLink')}
            </button>
            <div className="gate-links">
              <Link to="/sign-in">{t('backToSignIn')}</Link>
            </div>
          </form>
        </>
      )}
    </GateLayout>
  );
}
