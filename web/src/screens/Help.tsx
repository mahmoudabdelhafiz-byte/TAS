import { useMemo } from 'react';
import { guidesIn } from '../help/content.ts';
import { guidesFor } from '../help/guides.ts';
import { parseMarkdown } from '../lib/markdown.ts';
import { BlockView } from '../ui/Markdown.tsx';
import { screensFor } from '../lib/routes.ts';
import { useApp } from '../ui/context.tsx';

/** The user guides for the signed-in user's role, in the portal's language; each one prints on its own. */
export function Help() {
  const { t, lang, user, route, go, terminal } = useApp();
  const list = useMemo(() => guidesFor(screensFor(user.role, terminal.autogate ?? false)), [user.role, terminal.autogate]);
  const current = list.find(g => g.id === route.params.g) ?? list[0];
  const blocks = useMemo(() => parseMarkdown(guidesIn(lang)[current.id] ?? ''), [lang, current.id]);
  const titleOf = (id: string) => /^#\s+(.*)$/m.exec(guidesIn(lang)[id] ?? '')?.[1] ?? id;

  return (
    <section className="screen help">
      <header className="screen-head no-print">
        <h1>{t('help.title')}</h1>
        <div className="actions">
          <a className="btn btn-quiet" href="#/docs" target="_blank" rel="noreferrer">{t('help.allDocs')}</a>
          <button type="button" className="btn btn-secondary" onClick={() => window.print()}>{t('help.print')}</button>
        </div>
      </header>
      <div className="help-grid">
        <nav className="help-list no-print" aria-label={t('help.guides')}>
          <ul>
            {list.map(g => (
              <li key={g.id}>
                <a href={`#/help?g=${g.id}`} className={g.id === current.id ? 'active' : undefined}
                  aria-current={g.id === current.id ? 'page' : undefined}
                  onClick={e => { e.preventDefault(); go('help', { g: g.id }); }}>
                  {titleOf(g.id)}
                </a>
              </li>
            ))}
          </ul>
        </nav>
        <article className="guide" lang={lang}>
          {blocks.map((b, i) => <BlockView key={i} block={b} />)}
        </article>
      </div>
    </section>
  );
}
