import { useMemo, useState, type FormEvent } from 'react';
import { ApiError } from '../lib/api.ts';
import { splitContainers } from '../lib/plan.ts';
import { slotRange, stamp } from '../lib/time.ts';
import type { AdminResponse } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { LoadError, Notice, useLoad } from '../ui/kit.tsx';

type State = 'READY' | 'NOT_AVAILABLE' | 'ON_HOLD' | 'BOOKED' | 'COLLECTED' | 'INVALID';

interface TrackCheck { type: string; result: 'Pass' | 'Warn' | 'Fail'; message: string | null }

interface Tracked {
  containerNo: string;
  isoType: string | null;
  sizeFt: number | null;
  state: State;
  checks: TrackCheck[];
  problems: string[];
  warnings: string[];
  appointment: { appointmentNo: string; status: string; slotStart: string; slotEnd: string; gateOutAt: string | null } | null;
  bookedByOthers: boolean;
  following: boolean;
}

interface TrackResult { blNo: string | null; checkedAt: string; containers: Tracked[] }

interface Watch { containerNo: string; state: State | null; message: string | null; lastCheckedAt: string | null; readyNotifiedAt: string | null; createdAt: string }

/** The checks shown as columns, in the order a container gets cleared. */
const COLUMNS = ['Availability', 'CustomsRelease', 'LineRelease', 'TerminalHold', 'Payment'] as const;

export function TrackContainers() {
  const { api, t, tx, lang, terminal, go } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/containers`;
  const [source, setSource] = useState<'bl' | 'list'>('bl');
  const [blNo, setBlNo] = useState('');
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [result, setResult] = useState<TrackResult | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const followed = useLoad(() => api.get<Watch[]>(`${base}/followed`), [base]);
  const typed = useMemo(() => splitContainers(text), [text]);

  async function search(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setDone(null);
    try {
      const r = await api.post<AdminResponse<TrackResult>>(`${base}/track`, source === 'bl' ? { blNo: blNo.trim() } : { containers: typed });
      setResult(r.data);
      setSelected(r.data.containers.filter(c => c.state === 'READY').map(c => c.containerNo));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('common.offline'));
      setResult(null);
    } finally {
      setBusy(false);
    }
  }

  async function follow(numbers: string[]) {
    try {
      await api.post(`${base}/followed`, { containers: numbers });
      setDone(t('track.followedNote', { n: numbers.length }));
      setResult(r => r && { ...r, containers: r.containers.map(c => (numbers.includes(c.containerNo) ? { ...c, following: true } : c)) });
      followed.reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('common.offline'));
    }
  }

  async function unfollow(no: string) {
    try {
      await api.request('DELETE', `${base}/followed/${encodeURIComponent(no)}`);
      setResult(r => r && { ...r, containers: r.containers.map(c => (c.containerNo === no ? { ...c, following: false } : c)) });
      followed.reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('common.offline'));
    }
  }

  const rows = result?.containers ?? [];
  const counts = rows.reduce<Record<string, number>>((acc, c) => ({ ...acc, [c.state]: (acc[c.state] ?? 0) + 1 }), {});
  const notReady = rows.filter(c => (c.state === 'NOT_AVAILABLE' || c.state === 'ON_HOLD') && !c.following).map(c => c.containerNo);
  const bookable = selected.filter(no => rows.find(c => c.containerNo === no)?.state === 'READY');

  return (
    <section className="screen track">
      <header className="screen-head">
        <h1>{t('track.title')}</h1>
        <p className="head-figure">{t('track.lead')}</p>
      </header>

      <div className="track-grid">
        <div className="track-main">
          <form className="track-search" onSubmit={search}>
            <div className="segmented" role="group" aria-label={t('track.searchBy')}>
              <button type="button" className={source === 'bl' ? 'seg active' : 'seg'} onClick={() => setSource('bl')}>{t('track.byBl')}</button>
              <button type="button" className={source === 'list' ? 'seg active' : 'seg'} onClick={() => setSource('list')}>{t('track.byContainers')}</button>
            </div>
            {source === 'bl' ? (
              <label className="field"><span>{t('track.blNo')}</span>
                <input dir="ltr" value={blNo} onChange={e => setBlNo(e.target.value)} placeholder="MEDUJD260001" autoFocus /></label>
            ) : (
              <label className="field"><span>{t('track.containers')}</span>
                <textarea dir="ltr" rows={4} value={text} onChange={e => setText(e.target.value)} placeholder={'MSCU1234566\nTGHU8812340'} />
                <span className="hint">{t('track.containersHint', { n: typed.length })}</span></label>
            )}
            <button type="submit" className="btn btn-primary" disabled={busy || (source === 'bl' ? !blNo.trim() : typed.length === 0)}>
              {busy ? t('common.loading') : t('track.search')}
            </button>
          </form>

          {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
          {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}

          {result && (
            <>
              <div className="track-summary">
                <p className="muted">
                  {result.blNo ? <>{t('track.onBl')} <bdi className="num">{result.blNo}</bdi> · </> : null}
                  {t('track.checkedAt', { time: stamp(result.checkedAt, terminal.timeZone, lang) })}
                </p>
                <ul className="state-counts">
                  {(['READY', 'ON_HOLD', 'NOT_AVAILABLE', 'BOOKED', 'COLLECTED', 'INVALID'] as State[]).filter(s => counts[s]).map(s => (
                    <li key={s}><span className={`state-chip state-${s.toLowerCase()}`}>{tx('track.state', s)}</span> <span className="num">{counts[s]}</span></li>
                  ))}
                </ul>
              </div>

              <div className="table-wrap">
                <table className="grid compact track-table">
                  <thead>
                    <tr>
                      <th className="cell-check"><span className="visually-hidden">{t('track.select')}</span></th>
                      <th>{t('track.container')}</th>
                      <th>{t('track.status')}</th>
                      {COLUMNS.map(c => <th key={c} className="cell-mark">{tx('track.check', c)}</th>)}
                      <th>{t('track.details')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map(c => (
                      <tr key={c.containerNo}>
                        <td className="cell-check">
                          {c.state === 'READY' && (
                            <input type="checkbox" aria-label={c.containerNo} checked={selected.includes(c.containerNo)}
                              onChange={e => setSelected(s => (e.target.checked ? [...s, c.containerNo] : s.filter(x => x !== c.containerNo)))} />
                          )}
                        </td>
                        <td className="nowrap">
                          <bdi className="plate">{c.containerNo}</bdi>{c.sizeFt ? <span className="muted"> {c.sizeFt}′</span> : null}
                          {c.state !== 'INVALID' && c.state !== 'COLLECTED' && (
                            <div>
                              {c.following
                                ? <button type="button" className="link-btn following" onClick={() => unfollow(c.containerNo)} title={t('track.unfollow')}>✓ {t('track.following')}</button>
                                : <button type="button" className="link-btn" onClick={() => follow([c.containerNo])}>{t('track.follow')}</button>}
                            </div>
                          )}
                        </td>
                        <td><span className={`state-chip state-${c.state.toLowerCase()}`}>{tx('track.state', c.state)}</span></td>
                        {COLUMNS.map(col => {
                          const check = c.checks.find(k => k.type === col);
                          return (
                            <td key={col} className="cell-mark">
                              {check ? <span className={`mark mark-${check.result.toLowerCase()}`} title={check.message ?? undefined}
                                aria-label={`${tx('track.check', col)}: ${tx('track.result', check.result)}`} /> : <span className="muted">·</span>}
                            </td>
                          );
                        })}
                        <td className="track-details" dir="auto">
                          {c.appointment && (
                            <a href={`#/appointments?no=${c.appointment.appointmentNo}`} className="row-link">
                              <bdi>{c.appointment.appointmentNo}</bdi> · {slotRange(c.appointment.slotStart, c.appointment.slotEnd, terminal.timeZone)}
                            </a>
                          )}
                          {c.bookedByOthers && <span className="muted">{t('track.bookedByOthers')}</span>}
                          {c.problems.map(p => <div key={p} className="check-fail">{p}</div>)}
                          {c.warnings.map(w => <div key={w} className="warn-text">{w}</div>)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              <div className="actions">
                <button type="button" className="btn btn-primary" disabled={bookable.length === 0}
                  onClick={() => go('book', { containers: bookable.join(',') })}>{t('track.book', { n: bookable.length })}</button>
                {notReady.length > 0 && (
                  <button type="button" className="btn btn-secondary" onClick={() => follow(notReady)}>{t('track.followNotReady', { n: notReady.length })}</button>
                )}
              </div>
            </>
          )}
        </div>

        <aside className="track-side">
          <h2>{t('track.followedTitle')}</h2>
          <p className="hint">{t('track.followedHint')}</p>
          {followed.error && <LoadError error={followed.error} retry={followed.reload} />}
          {followed.data && followed.data.length === 0 && <p className="muted">{t('track.noneFollowed')}</p>}
          <ul className="watch-list">
            {(followed.data ?? []).map(w => (
              <li key={w.containerNo}>
                <div className="watch-main">
                  <bdi className="plate">{w.containerNo}</bdi>
                  {w.state ? <span className={`state-chip state-${w.state.toLowerCase()}`}>{tx('track.state', w.state)}</span> : <span className="muted">{t('track.notChecked')}</span>}
                </div>
                {w.message && <div className="watch-note" dir="auto">{w.message}</div>}
                <div className="watch-meta">
                  <span className="muted">{w.lastCheckedAt ? t('track.checkedAt', { time: stamp(w.lastCheckedAt, terminal.timeZone, lang) }) : ''}</span>
                  <button type="button" className="link-btn" onClick={() => unfollow(w.containerNo)}>{t('track.unfollow')}</button>
                </div>
              </li>
            ))}
          </ul>
        </aside>
      </div>
    </section>
  );
}
