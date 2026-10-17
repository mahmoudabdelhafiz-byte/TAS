import { useState, type FormEvent } from 'react';
import type { ApiClient } from '../lib/api.ts';
import { ApiError } from '../lib/api.ts';
import type { TextKey } from '../lib/i18n.ts';
import type { Lang } from '../lib/time.ts';
import type { UserProfile } from '../lib/types.ts';
import { BrandMark } from './Shell.tsx';
import type { Brand } from '../lib/brand.ts';
import { LanguageSwitch } from '../ui/LanguageSwitch.tsx';

export function Login({ api, lang, t, setLang, sessionEnded, onSignedIn, brand }: {
  api: ApiClient;
  brand: Brand;
  lang: Lang;
  t: (key: TextKey) => string;
  setLang: (l: Lang) => void;
  sessionEnded: boolean;
  onSignedIn: (user: UserProfile) => void;
}) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(sessionEnded ? t('common.sessionEnded') : null);
  const [forgot, setForgot] = useState(false);
  const [sent, setSent] = useState(false);
  const [challenge, setChallenge] = useState<string | null>(null);
  const [code, setCode] = useState('');

  async function askForLink(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.post('/api/auth/forgot-password', { email: email.trim() });
      setSent(true);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('common.offline'));
    } finally {
      setBusy(false);
    }
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      if (challenge) {
        onSignedIn(await api.loginCode(challenge, code));
        return;
      }
      const outcome = await api.login(email.trim(), password);
      if ('challenge' in outcome) {
        setChallenge(outcome.challenge);
        setPassword('');
      } else onSignedIn(outcome.user);
    } catch (err) {
      if (challenge && err instanceof ApiError && err.code === 'challenge_expired') {
        setChallenge(null);
        setCode('');
      }
      setError(err instanceof ApiError ? err.message : t('common.offline'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="login">
      <section className="login-yard" aria-hidden="true">
        <div className="login-brand"><BrandMark brand={brand} /> <span>{brand.name}</span></div>
        <YardPicture />
      </section>
      <section className="login-form-side">
        <div className="login-lang"><LanguageSwitch lang={lang} setLang={setLang} label={t('lang.switch')} /></div>
        {forgot ? (
          <form className="login-form" onSubmit={askForLink}>
            <h1>{t('login.forgotTitle')}</h1>
            <p className="lead">{t('login.forgotLead')}</p>
            {error && <div className="notice notice-error" role="alert">{error}</div>}
            {sent ? (
              <div className="notice notice-ok" role="status">{t('login.forgotSent')}</div>
            ) : (
              <>
                <label className="field">
                  <span>{t('login.email')}</span>
                  <input type="email" autoComplete="username" required value={email} onChange={e => setEmail(e.target.value)} dir="ltr" />
                </label>
                <button type="submit" className="btn btn-primary btn-wide" disabled={busy}>{t('login.forgotSubmit')}</button>
              </>
            )}
            <button type="button" className="link-btn" onClick={() => { setForgot(false); setSent(false); setError(null); }}>{t('login.backToSignIn')}</button>
          </form>
        ) : (
        challenge ? (
        <form className="login-form" onSubmit={submit}>
          <h1>{t('login.codeTitle')}</h1>
          <p className="lead">{t('login.codeLead')}</p>
          {error && <div className="notice notice-error" role="alert">{error}</div>}
          <label className="field">
            <span>{t('login.code')}</span>
            <input inputMode="numeric" autoComplete="one-time-code" required autoFocus maxLength={7} value={code}
              onChange={e => setCode(e.target.value.replace(/[^0-9 ]/g, ''))} dir="ltr" className="code-input" />
          </label>
          <button type="submit" className="btn btn-primary btn-wide" disabled={busy || code.replace(/\D/g, '').length !== 6}>
            {busy ? t('login.busy') : t('login.submit')}
          </button>
          <button type="button" className="link-btn" onClick={() => { setChallenge(null); setCode(''); setError(null); }}>{t('login.backToSignIn')}</button>
        </form>
        ) : (
        <form className="login-form" onSubmit={submit}>
          <h1>{t('login.title')}</h1>
          <p className="lead">{t('login.lead')}</p>
          {error && <div className="notice notice-error" role="alert">{error}</div>}
          <label className="field">
            <span>{t('login.email')}</span>
            <input type="email" autoComplete="username" required value={email} onChange={e => setEmail(e.target.value)} dir="ltr" />
          </label>
          <label className="field">
            <span>{t('login.password')}</span>
            <input type="password" autoComplete="current-password" required value={password} onChange={e => setPassword(e.target.value)} dir="ltr" />
          </label>
          <button type="submit" className="btn btn-primary btn-wide" disabled={busy}>
            {busy ? t('login.busy') : t('login.submit')}
          </button>
          <button type="button" className="link-btn" onClick={() => { setForgot(true); setError(null); }}>{t('login.forgot')}</button>
        </form>
        )
        )}
        <a className="login-docs" href="#/docs">{t('login.docs')}</a>
      </section>
    </main>
  );
}

/** Container stacks seen from the side: the hours of a day filling up. Purely decorative. */
function YardPicture() {
  const stacks = [3, 5, 4, 6, 2, 5, 3, 4, 6, 5, 1, 4];
  return (
    <svg className="yard-picture" viewBox="0 0 360 200" preserveAspectRatio="xMidYMax meet">
      {stacks.map((h, i) =>
        Array.from({ length: h }, (_, j) => (
          <rect key={`${i}-${j}`} x={6 + i * 29.5} y={188 - (j + 1) * 26} width="25" height="22" rx="1.5"
            className={j === h - 1 && i % 3 === 1 ? 'yard-box yard-box-accent' : 'yard-box'} />
        )),
      )}
      <line x1="0" y1="190" x2="360" y2="190" className="yard-ground" />
    </svg>
  );
}
