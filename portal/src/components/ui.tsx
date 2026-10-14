import { useId, type InputHTMLAttributes, type ReactNode } from 'react';
import { ApiError } from '../api/client';
import type { TrafficLight } from '../api/types';
import { useI18n } from '../i18n/I18n';
import type { MessageKey } from '../i18n/en';

interface FieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string;
  hint?: string;
  error?: string | null;
}

/** Label, input, hint and error, wired up for screen readers. */
export function Field({ label, hint, error, ...input }: FieldProps) {
  const id = useId();
  const hintId = hint ? `${id}-hint` : undefined;
  const errorId = error ? `${id}-error` : undefined;
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input
        id={id}
        aria-invalid={error ? true : undefined}
        aria-describedby={[hintId, errorId].filter(Boolean).join(' ') || undefined}
        {...input}
      />
      {hint && (
        <span id={hintId} className="hint">
          {hint}
        </span>
      )}
      {error && (
        <span id={errorId} className="hint" style={{ color: 'var(--hold)' }}>
          {error}
        </span>
      )}
    </div>
  );
}

export function Notice({ kind = 'info', children }: { kind?: 'info' | 'error' | 'success'; children: ReactNode }) {
  return (
    <div className={`notice ${kind === 'info' ? '' : kind}`} role={kind === 'error' ? 'alert' : 'status'}>
      {children}
    </div>
  );
}

const lightKeys: Record<TrafficLight, MessageKey> = {
  Green: 'lightGreen',
  Amber: 'lightAmber',
  Red: 'lightRed',
  Grey: 'lightGrey',
};

export function StatusLight({ light }: { light: TrafficLight }) {
  const { t } = useI18n();
  return (
    <span className="light" data-light={light}>
      {t(lightKeys[light])}
    </span>
  );
}

export function lightLabelKey(light: TrafficLight): MessageKey {
  return lightKeys[light];
}

/** A failed query with the server's reason and a retry. */
export function LoadError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const { t } = useI18n();
  const detail = error instanceof ApiError ? error.message : '';
  return (
    <Notice kind="error">
      <p>{t('loadFailed', { detail })}</p>
      <button type="button" className="button quiet" onClick={onRetry}>
        {t('retry')}
      </button>
    </Notice>
  );
}

export function LanguageSwitch() {
  const { t, language, setLanguage } = useI18n();
  return (
    <button
      type="button"
      className="button quiet"
      lang={language === 'ar' ? 'en' : 'ar'}
      onClick={() => setLanguage(language === 'ar' ? 'en' : 'ar')}
    >
      {t('switchLanguage')}
    </button>
  );
}
