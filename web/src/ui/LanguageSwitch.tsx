import { portalLanguages } from '../lib/languages.ts';
import type { Lang } from '../lib/time.ts';

/**
 * English ⇄ Arabic as one link while those are the portal's only languages; a list once language packs add more.
 * Each language is named in itself, so anyone can find their own.
 */
export function LanguageSwitch({ lang, setLang, label, className = 'link-btn' }: {
  lang: Lang;
  setLang: (lang: Lang) => void;
  /** The toggle's text (t('lang.switch')); the list's accessible name. */
  label: string;
  className?: string;
}) {
  const options = portalLanguages();
  if (options.length <= 2) {
    const other = options.find(l => l.code !== lang)?.code ?? (lang === 'en' ? 'ar' : 'en');
    return (
      <button type="button" className={className} lang={other} onClick={() => setLang(other)}>
        {options.find(l => l.code === other)?.name ?? label}
      </button>
    );
  }
  return (
    <select className="lang-select" aria-label={label} value={lang} onChange={e => setLang(e.target.value)}>
      {options.map(l => <option key={l.code} value={l.code} lang={l.code}>{l.name}</option>)}
    </select>
  );
}
