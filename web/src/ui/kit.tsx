// Small shared pieces: data loading, traffic light, day stepper, messages, side panel.

import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { ApiError } from '../lib/api.ts';
import { addDays, dayLabel } from '../lib/time.ts';
import type { Light } from '../lib/types.ts';
import { useApp } from './context.tsx';

export interface Loaded<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
  reload: () => void;
}

/** Runs `load` whenever `deps` change; keeps the previous data while reloading so tables don't flash. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[], refreshMs?: number): Loaded<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  const { t } = useApp();
  const loadRef = useRef(load);
  loadRef.current = load;

  useEffect(() => {
    let live = true;
    setLoading(true);
    loadRef.current()
      .then(result => { if (live) { setData(result); setError(null); } })
      .catch(e => { if (live) setError(messageOf(e, t('common.offline'))); })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [...deps, tick]);

  useEffect(() => {
    if (!refreshMs) return;
    const id = setInterval(() => setTick(n => n + 1), refreshMs);
    return () => clearInterval(id);
  }, [refreshMs]);

  const reload = useCallback(() => setTick(n => n + 1), []);
  return { data, error, loading, reload };
}

export function messageOf(e: unknown, offline: string): string {
  if (e instanceof ApiError) return e.message;
  return offline;
}

export function TrafficLight({ light, reason }: { light: Light; reason?: string }) {
  const { tx } = useApp();
  return (
    <span className={`light light-${light.toLowerCase()}`} title={reason}>
      <span className="light-dot" aria-hidden="true" />
      <span>{tx('light', light)}</span>
    </span>
  );
}

export function DayStepper({ date, onChange, min, max }: { date: string; onChange: (d: string) => void; min?: string; max?: string }) {
  const { t, lang, today } = useApp();
  return (
    <div className="day-stepper" role="group" aria-label={t('common.day')}>
      <button type="button" className="icon-btn flip" aria-label={t('common.prevDay')}
        disabled={!!min && date <= min} onClick={() => onChange(addDays(date, -1))}>
        <Chevron />
      </button>
      <label className="day-label">
        <span>{date === today ? `${t('common.today')}${t('common.sep')}${dayLabel(date, lang)}` : dayLabel(date, lang)}</span>
        <input type="date" value={date} min={min} max={max}
          onChange={e => e.target.value && onChange(e.target.value)} aria-label={t('common.day')} />
      </label>
      <button type="button" className="icon-btn" aria-label={t('common.nextDay')}
        disabled={!!max && date >= max} onClick={() => onChange(addDays(date, 1))}>
        <Chevron />
      </button>
    </div>
  );
}

/** Points to the inline end (right in English, left in Arabic via CSS). */
function Chevron() {
  return (
    <svg className="chevron" width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
      <path d="M6 3l5 5-5 5" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

export function Notice({ kind, children, onClose }: { kind: 'ok' | 'error' | 'info'; children: ReactNode; onClose?: () => void }) {
  const { t } = useApp();
  return (
    <div className={`notice notice-${kind}`} role={kind === 'error' ? 'alert' : 'status'}>
      <div dir="auto">{children}</div>
      {onClose && <button type="button" className="link-btn" onClick={onClose}>{t('common.close')}</button>}
    </div>
  );
}

export function LoadError({ error, retry }: { error: string; retry: () => void }) {
  const { t } = useApp();
  return (
    <Notice kind="error">
      {error} <button type="button" className="link-btn" onClick={retry}>{t('common.retry')}</button>
    </Notice>
  );
}

export function Panel({ title, onClose, children }: { title: ReactNode; onClose: () => void; children: ReactNode }) {
  const { t } = useApp();
  const ref = useRef<HTMLElement>(null);
  useEffect(() => {
    ref.current?.focus();
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return (
    <aside className="panel" ref={ref} tabIndex={-1} aria-label={typeof title === 'string' ? title : undefined}>
      <header className="panel-head">
        <h2>{title}</h2>
        <button type="button" className="icon-btn" onClick={onClose} aria-label={t('common.close')}>
          <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M3.5 3.5l9 9m0-9l-9 9" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
          </svg>
        </button>
      </header>
      <div className="panel-body">{children}</div>
    </aside>
  );
}

/** A button that shows a busy state while its async action runs and reports failures to `onError`. */
export function ActionButton({ onRun, children, kind = 'secondary', disabled, onError }: {
  onRun: () => Promise<unknown>;
  children: ReactNode;
  kind?: 'primary' | 'secondary' | 'danger';
  disabled?: boolean;
  onError: (message: string) => void;
}) {
  const [busy, setBusy] = useState(false);
  const { t } = useApp();
  return (
    <button type="button" className={`btn btn-${kind}`} disabled={disabled || busy} aria-busy={busy}
      onClick={async () => {
        setBusy(true);
        try { await onRun(); } catch (e) { onError(messageOf(e, t('common.offline'))); } finally { setBusy(false); }
      }}>
      {children}
    </button>
  );
}
