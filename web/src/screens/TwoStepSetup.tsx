import { useState, type FormEvent } from 'react';
import type { ApiClient } from '../lib/api.ts';
import { ApiError } from '../lib/api.ts';
import type { TextKey } from '../lib/i18n.ts';
import type { Lang } from '../lib/time.ts';
import type { UserProfile } from '../lib/types.ts';
import type { Brand } from '../lib/brand.ts';
import { BrandMark } from './Shell.tsx';
import { LanguageSwitch } from '../ui/LanguageSwitch.tsx';

interface Setup { secret: string; uri: string }

/**
 * Admins sign in with a code from an authenticator app. Before anything else, a new admin adds the account to the app
 * (the key typed in, or the link opened on the phone) and confirms with the first code.
 */
export function TwoStepSetup({ api, user, lang, t, setLang, brand, onDone, onSignOut }: {
  api: ApiClient;
  user: UserProfile;
  lang: Lang;
  t: (key: TextKey) => string;
  setLang: (l: Lang) => void;
  brand: Brand;
  onDone: (user: UserProfile) => void;
  onSignOut: () => void;
}) {
  const [setup, setSetup] = useState<Setup | null>(null);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('common.offline'));
    } finally {
      setBusy(false);
    }
  }

  const start = () => run(async () => setSetup((await api.post<{ data: Setup }>('/api/auth/two-step/setup')).data));

  const confirm = (e: FormEvent) => {
    e.preventDefault();
    return run(async () => {
      await api.post('/api/auth/two-step/confirm', { code });
      const updated = { ...user, twoStepEnabled: true, twoStepSetupRequired: false };
      api.updateUser(updated);
      onDone(updated);
    });
  };

  return (
    <main className="login single">
      <section className="login-form-side">
        <div className="login-lang"><LanguageSwitch lang={lang} setLang={setLang} label={t('lang.switch')} /></div>
        <div className="login-brand dark"><BrandMark brand={brand} /> <span>{brand.name}</span></div>
        <form className="login-form" onSubmit={confirm}>
          <h1>{t('twostep.title')}</h1>
          <p className="lead">{t('twostep.lead')}</p>
          {error && <div className="notice notice-error" role="alert">{error}</div>}
          {!setup ? (
            <button type="button" className="btn btn-primary btn-wide" disabled={busy} onClick={start}>{t('twostep.start')}</button>
          ) : (
            <>
              <ol className="twostep-steps">
                <li>{t('twostep.step1')}</li>
                <li>
                  {t('twostep.step2')}
                  <code className="twostep-key" dir="ltr">{setup.secret}</code>
                  <span className="twostep-actions">
                    <button type="button" className="link-btn" onClick={() => navigator.clipboard?.writeText(setup.secret.replace(/ /g, ''))}>{t('twostep.copy')}</button>
                    <a className="link-btn" href={setup.uri}>{t('twostep.openApp')}</a>
                  </span>
                </li>
                <li>{t('twostep.step3')}</li>
              </ol>
              <label className="field">
                <span>{t('login.code')}</span>
                <input inputMode="numeric" autoComplete="one-time-code" required maxLength={7} value={code}
                  onChange={e => setCode(e.target.value.replace(/[^0-9 ]/g, ''))} dir="ltr" className="code-input" />
              </label>
              <button type="submit" className="btn btn-primary btn-wide" disabled={busy || code.replace(/\D/g, '').length !== 6}>{t('twostep.confirm')}</button>
            </>
          )}
          <button type="button" className="link-btn" onClick={onSignOut}>{t('nav.signOut')}</button>
        </form>
      </section>
    </main>
  );
}
