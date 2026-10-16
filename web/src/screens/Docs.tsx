import { useMemo } from 'react';
import { guideText, techText } from '../help/content.ts';
import { guides, techPages } from '../help/guides.ts';
import type { TextKey } from '../lib/i18n.ts';
import { parseMarkdown } from '../lib/markdown.ts';
import type { Lang } from '../lib/time.ts';
import { BlockView } from '../ui/Markdown.tsx';
import { Mark } from './Shell.tsx';

/** The public documentation (#/docs/<page>): every user guide and the technical pages, no sign-in needed. */
export function Docs({ page, lang, setLang, t, signedIn }: {
  page: string;
  lang: Lang;
  setLang: (l: Lang) => void;
  t: (key: TextKey, values?: Record<string, string | number>) => string;
  signedIn: boolean;
}) {
  const isTech = (techPages as readonly string[]).includes(page);
  const current = isTech || guides.some(g => g.id === page) ? page : 'start';
  const source = isTech ? techText[current] : guideText[lang][current];
  const blocks = useMemo(() => parseMarkdown(source ?? ''), [source]);
  const titleOf = (text: string | undefined, id: string) => /^#\s+(.*)$/m.exec(text ?? '')?.[1] ?? id;
  const link = (id: string, text: string | undefined) => (
    <li key={id}>
      <a href={`#/docs/${id}`} className={id === current ? 'active' : undefined} aria-current={id === current ? 'page' : undefined}>
        {titleOf(text, id)}
      </a>
    </li>
  );

  return (
    <div className="docs">
      <header className="docs-bar no-print">
        <a href="#/docs" className="docs-brand"><Mark /><span>{t('docs.title')}</span></a>
        <div className="docs-bar-end">
          <button type="button" className="link-btn" lang={lang === 'en' ? 'ar' : 'en'} onClick={() => setLang(lang === 'en' ? 'ar' : 'en')}>
            {t('lang.switch')}
          </button>
          <a className="btn btn-secondary" href="#/">{signedIn ? t('docs.open') : t('login.submit')}</a>
        </div>
      </header>
      <div className="docs-body">
        <nav className="help-list no-print" aria-label={t('docs.title')}>
          <h2>{t('docs.users')}</h2>
          <ul>{guides.map(g => link(g.id, guideText[lang][g.id]))}</ul>
          <h2>{t('docs.it')}</h2>
          <ul lang="en" dir="ltr">{techPages.map(id => link(id, techText[id]))}</ul>
        </nav>
        <main>
          {isTech && lang !== 'en' && <p className="hint no-print">{t('docs.englishOnly')}</p>}
          <article className="guide" lang={isTech ? 'en' : lang} dir={isTech ? 'ltr' : undefined}>
            {blocks.map((b, i) => <BlockView key={i} block={b} />)}
          </article>
          <p className="no-print docs-print">
            <button type="button" className="btn btn-quiet" onClick={() => window.print()}>{t('help.print')}</button>
          </p>
        </main>
      </div>
    </div>
  );
}
