// The installation's languages: English and Arabic are built into the portal; language packs on the server add more
// (or correct the built-in texts) without a new version. GET /api/languages lists them, GET /api/languages/{code}
// gives a pack's texts.

import { addDictionary } from './i18n.ts';

export interface LanguageInfo {
  code: string;
  name: string;
  direction: 'ltr' | 'rtl';
  /** Has portal texts (built in, or in a pack). */
  portal: boolean;
  app: boolean;
  /** People can get messages (SMS, email, push) in it. */
  messages: boolean;
  fromPack: boolean;
}

export const builtInLanguages: LanguageInfo[] = [
  { code: 'en', name: 'English', direction: 'ltr', portal: true, app: true, messages: true, fromPack: false },
  { code: 'ar', name: 'العربية', direction: 'rtl', portal: true, app: true, messages: true, fromPack: false },
  { code: 'ur', name: 'اردو', direction: 'rtl', portal: false, app: true, messages: true, fromPack: false },
  { code: 'hi', name: 'हिन्दी', direction: 'ltr', portal: false, app: true, messages: true, fromPack: false },
];

let catalog: LanguageInfo[] = builtInLanguages;

export const languages = (): LanguageInfo[] => catalog;
export const portalLanguages = (): LanguageInfo[] => catalog.filter(l => l.portal);
export const messageLanguages = (): LanguageInfo[] => catalog.filter(l => l.messages);
export const isPortalLanguage = (code: string | null | undefined): boolean => portalLanguages().some(l => l.code === code);

export function direction(code: string): 'ltr' | 'rtl' {
  return catalog.find(l => l.code === code)?.direction ?? (code === 'ar' || code === 'ur' ? 'rtl' : 'ltr');
}

export function languageName(code: string): string {
  return catalog.find(l => l.code === code)?.name ?? code;
}

type Fetch = (input: string) => Promise<{ ok: boolean; json(): Promise<unknown> }>;

/** Reads the installation's languages and the portal texts of its packs. Without the server the built-in ones stay. */
export async function loadLanguages(fetcher: Fetch = input => fetch(input)): Promise<LanguageInfo[]> {
  try {
    const response = await fetcher('/api/languages');
    if (!response.ok) return catalog;
    const list = (await response.json()) as LanguageInfo[];
    await Promise.all(list.filter(l => l.fromPack && l.portal).map(async l => {
      const pack = await fetcher(`/api/languages/${encodeURIComponent(l.code)}`);
      if (pack.ok) addDictionary(l.code, ((await pack.json()) as { portal?: Record<string, string> }).portal ?? {});
    }));
    catalog = list.length > 0 ? list : builtInLanguages;
  } catch {
    // offline or an older server: the built-in languages
  }
  return catalog;
}
