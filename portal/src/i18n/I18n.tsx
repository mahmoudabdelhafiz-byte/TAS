import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ar } from './ar';
import { en, type MessageKey, type Messages } from './en';

export type Language = 'ar' | 'en';

const dictionaries: Record<Language, Messages> = { ar, en };
const STORAGE_KEY = 'tas.lang';

/** Fills {placeholders}; unknown placeholders stay visible so a missing value is noticed. */
export function format(text: string, vars?: Record<string, string | number>): string {
  if (!vars) return text;
  return text.replace(/\{(\w+)\}/g, (match, name: string) => (name in vars ? String(vars[name]) : match));
}

export function translate(language: Language, key: MessageKey, vars?: Record<string, string | number>): string {
  return format(dictionaries[language][key], vars);
}

/**
 * Locale for dates and numbers. Arabic uses the Gregorian calendar and Latin digits, like the gate screens,
 * SMS messages and container paperwork, so a time reads the same everywhere.
 */
export function locale(language: Language): string {
  return language === 'ar' ? 'ar-SA-u-ca-gregory-nu-latn' : 'en-GB';
}

export function readStoredLanguage(): Language | null {
  try {
    const value = localStorage.getItem(STORAGE_KEY);
    return value === 'ar' || value === 'en' ? value : null;
  } catch {
    return null;
  }
}

interface I18nValue {
  language: Language;
  dir: 'rtl' | 'ltr';
  t: (key: MessageKey, vars?: Record<string, string | number>) => string;
  setLanguage: (language: Language) => void;
  /** Date / time in the given IANA time zone (the terminal's), in the UI language. */
  formatDateTime: (value: Date | string, options: Intl.DateTimeFormatOptions, timeZone?: string) => string;
}

const I18nContext = createContext<I18nValue | null>(null);

export function I18nProvider({ children, initial }: { children: ReactNode; initial?: Language }) {
  // Arabic first: the portal opens in Arabic unless the person chose English before.
  const [language, setLanguageState] = useState<Language>(initial ?? readStoredLanguage() ?? 'ar');

  const setLanguage = useCallback((next: Language) => {
    setLanguageState(next);
    try {
      localStorage.setItem(STORAGE_KEY, next);
    } catch {
      // private mode: the choice lasts for this visit only
    }
  }, []);

  const dir = language === 'ar' ? 'rtl' : 'ltr';

  useEffect(() => {
    document.documentElement.lang = language;
    document.documentElement.dir = dir;
    document.title = dictionaries[language].appName;
  }, [language, dir]);

  const value = useMemo<I18nValue>(
    () => ({
      language,
      dir,
      setLanguage,
      t: (key, vars) => translate(language, key, vars),
      formatDateTime: (value, options, timeZone) =>
        new Intl.DateTimeFormat(locale(language), { ...options, timeZone }).format(typeof value === 'string' ? new Date(value) : value),
    }),
    [language, dir, setLanguage],
  );

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18nValue {
  const value = useContext(I18nContext);
  if (!value) throw new Error('useI18n must be used inside <I18nProvider>');
  return value;
}
