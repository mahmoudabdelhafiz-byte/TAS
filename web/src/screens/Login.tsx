import { useState, type FormEvent } from 'react';
import type { ApiClient } from '../lib/api.ts';
import { ApiError } from '../lib/api.ts';
import type { TextKey } from '../lib/i18n.ts';
import type { Lang } from '../lib/time.ts';
import type { UserProfile } from '../lib/types.ts';
import { Mark } from './Shell.tsx';

export function Login({ api, lang, t, setLang, sessionEnded, onSignedIn }: {
  api: ApiClient;
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

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      onSignedIn(await api.login(email.trim(), password));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('common.offline'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="login">
      <section className="login-yard" aria-hidden="true">
        <div className="login-brand"><Mark /> <span>Yardsyncs</span></div>
        <YardPicture />
      </section>
      <section className="login-form-side">
        <button type="button" className="link-btn login-lang" onClick={() => setLang(lang === 'en' ? 'ar' : 'en')}
          lang={lang === 'en' ? 'ar' : 'en'}>
          {t('lang.switch')}
        </button>
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
        </form>
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
