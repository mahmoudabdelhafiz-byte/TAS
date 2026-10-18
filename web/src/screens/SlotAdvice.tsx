import { dayLabel, locale, type Lang } from '../lib/time.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, useLoad } from '../ui/kit.tsx';

export type AdviceKind = 'Overbook' | 'LowerCapacity' | 'RaiseCapacity' | 'BusyDay';

export interface SlotSuggestion {
  key: string;
  kind: AdviceKind;
  day: string | null; // Sunday … Saturday
  start: string | null;
  end: string | null;
  date: string | null;
  current: number;
  suggested: number;
  sample: number;
  noShowRate: number | null;
  avgWaitMinutes: number | null;
  utilization: number | null;
  forecast: number | null;
}

export interface SlotAdvice {
  mode: number;
  historyDays: number;
  visits: number;
  enoughHistory: boolean;
  suggestions: SlotSuggestion[];
}

const MIN_HISTORY_DAYS = 28;
const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

export function weekdayName(day: string, lang: Lang): string {
  const i = Math.max(0, days.indexOf(day));
  // 18 October 2026 was a Sunday.
  return new Intl.DateTimeFormat(locale(lang), { weekday: 'long', timeZone: 'UTC' }).format(new Date(Date.UTC(2026, 9, 18 + i)));
}

const pct = (x: number | null) => Math.round((x ?? 0) * 100);
const num = (x: number) => (Number.isInteger(x) ? String(x) : x.toFixed(1));

/** What a suggestion proposes and why, in the reader's language. */
export function adviceText(s: SlotSuggestion, t: ReturnType<typeof useApp>['t'], lang: Lang): { when: string; what: string; why: string } {
  const when = s.kind === 'BusyDay'
    ? dayLabel(s.date ?? '', lang)
    : t('advice.window', { day: weekdayName(s.day ?? '', lang), start: s.start ?? '', end: s.end ?? '' });
  switch (s.kind) {
    case 'Overbook':
      return { when, what: t('advice.Overbook', { from: num(s.current), to: num(s.suggested) }), why: t('advice.Overbook.why', { rate: pct(s.noShowRate), n: s.sample }) };
    case 'LowerCapacity':
      return { when, what: t('advice.LowerCapacity', { from: s.current, to: s.suggested }), why: t('advice.LowerCapacity.why', { wait: Math.round(s.avgWaitMinutes ?? 0), n: s.sample }) };
    case 'RaiseCapacity':
      return { when, what: t('advice.RaiseCapacity', { from: s.current, to: s.suggested }), why: t('advice.RaiseCapacity.why', { util: pct(s.utilization), wait: Math.round(s.avgWaitMinutes ?? 0) }) };
    default:
      return { when, what: t('advice.BusyDay', { forecast: s.forecast ?? 0, from: s.current }), why: t('advice.BusyDay.why', { n: s.sample, extra: s.suggested - s.current }) };
  }
}

/** Suggestions from the terminal's own history on the slot board: the planner approves or dismisses each one. */
export function SlotAdvicePanel({ editable, onChanged, onError }: { editable: boolean; onChanged: (message: string) => void; onError: (m: string) => void }) {
  const { api, t, lang, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/capacity/advice`;
  const advice = useLoad(() => api.get<SlotAdvice>(base), [base]);

  if (advice.error) return <LoadError error={advice.error} retry={advice.reload} />;
  const a = advice.data;
  if (!a || a.mode === 0) return null;

  async function decide(key: string, approve: boolean) {
    await api.post(`${base}/decide`, { key, approve });
    advice.reload();
    onChanged(t(approve ? 'advice.approved' : 'advice.dismissed'));
  }

  return (
    <>
      <h2 className="section-title">{t('advice.title')}</h2>
      <p className="muted">{t('advice.lead')}</p>
      {!a.enoughHistory && <p className="muted">{t('advice.notYet', { min: MIN_HISTORY_DAYS, n: a.historyDays })}</p>}
      {a.enoughHistory && a.suggestions.length === 0 && <p className="muted">{t('advice.none')}</p>}
      {a.suggestions.length > 0 && (
        <ul className="advice">
          {a.suggestions.map(s => {
            const text = adviceText(s, t, lang);
            return (
              <li key={s.key} className={`advice-${s.kind}`}>
                <span className="advice-when">{text.when}</span>
                <span className="advice-what"><strong>{text.what}</strong><span className="muted">{text.why}</span></span>
                {editable && (
                  <span className="advice-actions">
                    <ActionButton kind="primary" onError={onError} onRun={() => decide(s.key, true)}>{t('advice.approve')}</ActionButton>
                    <ActionButton kind="secondary" onError={onError} onRun={() => decide(s.key, false)}>{t('advice.dismiss')}</ActionButton>
                  </span>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </>
  );
}
