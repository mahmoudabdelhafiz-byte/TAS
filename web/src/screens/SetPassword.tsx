import { useEffect, useState, type FormEvent } from 'react';
import type { ApiClient } from '../lib/api.ts';
import { ApiError } from '../lib/api.ts';
import type { TextKey } from '../lib/i18n.ts';
import type { Lang } from '../lib/time.ts';
import { BrandMark } from './Shell.tsx';
import type { Brand } from '../lib/brand.ts';
import { isPortalLanguage } from '../lib/languages.ts';
import { LanguageSwitch } from '../ui/LanguageSwitch.tsx';

interface LinkInfo {
  purpose: 'RESET' | 'INVITE';
  fullName: string;
  email: string;
  preferredLanguage: string;
}

/** The page behind the emailed link: set the first password (invitation) or a new one (forgot password). */
export function SetPassword({ api, token, lang, t, setLang, onDone, brand }: {
  api: ApiClient;
  brand: Brand;
  token: string;
  lang: Lang;
  t: (key: TextKey, values?: Record<string, string | number>) => string;
  setLang: (l: Lang) => void;
  onDone: () => void;
}) {
  const [info, setInfo] = useState<LinkInfo | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [password, setPassword] = useState('');
  const [again, setAgain] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  useEffect(() => {
    let live = true;
    fetch(`/api/auth/password-link?token=${encodeURIComponent(token)}`, { headers: { Accept: 'application/json' } })
      .then(async r => {
        if (!live) return;
        if (r.ok) {
          const data = (await r.json()) as LinkInfo;
          setInfo(data);
          if (isPortalLanguage(data.preferredLanguage)) setLang(data.preferredLanguage);
        } else {
          setProblem(t('password.badLink'));
        }
      })
      .catch(() => { if (live) setProblem(t('common.offline')); });
    return () => { live = false; };
  }, [token]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (password !== again) { setError(t('password.mismatch')); return; }
    setBusy(true);
    setError(null);
    try {
      await api.post('/api/auth/password-link', { token, newPassword: password });
      setDone(true);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('common.offline'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="login single">
      <section className="login-form-side">
        <div className="login-lang"><LanguageSwitch lang={lang} setLang={setLang} label={t('lang.switch')} /></div>
        <div className="login-form">
          <div className="login-brand dark"><BrandMark brand={brand} /> <span>{brand.name}</span></div>
          {problem && (
            <>
              <h1>{t('password.titleReset')}</h1>
              <div className="notice notice-error" role="alert">{problem}</div>
              <button type="button" className="btn btn-secondary btn-wide" onClick={onDone}>{t('login.backToSignIn')}</button>
            </>
          )}
          {!problem && !info && <p className="muted">{t('common.loading')}</p>}
          {info && done && (
            <>
              <h1>{t('password.doneTitle')}</h1>
              <div className="notice notice-ok" role="status">{t('password.done')}</div>
              <button type="button" className="btn btn-primary btn-wide" onClick={onDone}>{t('login.submit')}</button>
            </>
          )}
          {info && !done && (
            <form className="form-stack" onSubmit={submit}>
              <h1>{info.purpose === 'INVITE' ? t('password.titleInvite', { name: info.fullName }) : t('password.titleReset')}</h1>
              <p className="lead">{info.purpose === 'INVITE' ? t('password.leadInvite') : t('password.leadReset')} <bdi dir="ltr">{info.email}</bdi></p>
              {error && <div className="notice notice-error" role="alert">{error}</div>}
              <input type="email" autoComplete="username" value={info.email} readOnly hidden />
              <label className="field">
                <span>{t('password.new')}</span>
                <input type="password" autoComplete="new-password" required minLength={10} value={password} onChange={e => setPassword(e.target.value)} dir="ltr" />
              </label>
              <label className="field">
                <span>{t('password.again')}</span>
                <input type="password" autoComplete="new-password" required value={again} onChange={e => setAgain(e.target.value)} dir="ltr" />
              </label>
              <p className="hint">{t('password.rule')}</p>
              <button type="submit" className="btn btn-primary btn-wide" disabled={busy || password.length < 10}>{t('password.save')}</button>
            </form>
          )}
        </div>
      </section>
    </main>
  );
}
