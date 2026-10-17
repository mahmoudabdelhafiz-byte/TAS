import { useState } from 'react';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, Notice, useLoad } from '../ui/kit.tsx';

interface Subscription {
  subscriptionId: number;
  email: string;
  language: string;
  daily: boolean;
  weekly: boolean;
  lastDailyOn: string | null;
  lastWeeklyOn: string | null;
}

const weekdays = [0, 1, 2, 3, 4, 5, 6];

/** Who gets the daily and weekly reports by email, and when they go out (planners and terminal admins). */
export function ReportMail() {
  const { api, t, lang, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/report-subscriptions`;
  const settingsPath = `/api/terminals/${terminal.terminalId}/settings`;
  const list = useLoad(() => api.get<Subscription[]>(base), [base]);
  const settings = useLoad(() => api.get<Record<string, number>>(settingsPath), [settingsPath]);
  const [email, setEmail] = useState('');
  const [language, setLanguage] = useState<string>(lang === 'ar' ? 'ar' : 'en');
  const [daily, setDaily] = useState(true);
  const [weekly, setWeekly] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const dayName = (d: number) => new Intl.DateTimeFormat(lang === 'ar' ? 'ar' : 'en-GB', { weekday: 'long', timeZone: 'UTC' })
    .format(new Date(Date.UTC(2026, 9, 18 + d))); // 18 Oct 2026 is a Sunday

  const saveTime = async (key: 'report_hour' | 'report_weekday', value: number) => {
    setError(null);
    try {
      await api.put(settingsPath, { values: { [key]: value }, reason: null });
      settings.reload();
      setDone(t('slots.applied'));
    } catch (e) {
      setError(e instanceof Error ? e.message : t('common.offline'));
    }
  };

  return (
    <section className="report-mail">
      <h2 className="section-title">{t('mail.title')}</h2>
      <p className="hint">{t('mail.lead')}</p>
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
      {list.error && <LoadError error={list.error} retry={list.reload} />}

      {settings.data && (
        <div className="mail-when">
          <label className="field field-inline"><span>{t('mail.hour')}</span>
            <select value={settings.data.report_hour ?? 7} onChange={e => void saveTime('report_hour', Number(e.target.value))}>
              {Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
            </select></label>
          <label className="field field-inline"><span>{t('mail.weekday')}</span>
            <select value={settings.data.report_weekday ?? 0} onChange={e => void saveTime('report_weekday', Number(e.target.value))}>
              {weekdays.map(d => <option key={d} value={d}>{dayName(d)}</option>)}
            </select></label>
        </div>
      )}

      {list.data && list.data.length > 0 && (
        <div className="table-wrap">
          <table className="grid compact">
            <thead><tr><th>{t('mail.email')}</th><th>{t('mail.language')}</th><th>{t('mail.daily')}</th><th>{t('mail.weekly')}</th><th /></tr></thead>
            <tbody>
              {list.data.map(s => (
                <tr key={s.subscriptionId}>
                  <td><bdi>{s.email}</bdi></td>
                  <td>{s.language === 'ar' ? 'العربية' : 'English'}</td>
                  <td>{s.daily ? '✓' : ''}</td>
                  <td>{s.weekly ? '✓' : ''}</td>
                  <td>
                    <ActionButton kind="secondary" onError={setError} onRun={async () => {
                      await api.del(`${base}/${s.subscriptionId}`);
                      list.reload();
                    }}>{t('mail.remove')}</ActionButton>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <form className="mail-add" onSubmit={async e => {
        e.preventDefault();
        setError(null);
        try {
          await api.post(base, { email, language, daily, weekly });
          setEmail('');
          list.reload();
          setDone(t('mail.added'));
        } catch (err) {
          setError(err instanceof Error ? err.message : t('common.offline'));
        }
      }}>
        <label className="field"><span>{t('mail.email')}</span>
          <input type="email" value={email} required dir="ltr" onChange={e => setEmail(e.target.value)} /></label>
        <label className="field"><span>{t('mail.language')}</span>
          <select value={language} onChange={e => setLanguage(e.target.value)}>
            <option value="en">English</option>
            <option value="ar">العربية</option>
          </select></label>
        <label className="check"><input type="checkbox" checked={daily} onChange={e => setDaily(e.target.checked)} /> {t('mail.daily')}</label>
        <label className="check"><input type="checkbox" checked={weekly} onChange={e => setWeekly(e.target.checked)} /> {t('mail.weekly')}</label>
        <button type="submit" className="btn btn-primary" disabled={!email.trim() || (!daily && !weekly)}>{t('mail.add')}</button>
      </form>
    </section>
  );
}
