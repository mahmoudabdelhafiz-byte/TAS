import { Fragment, useMemo, type ReactNode } from 'react';
import { guideText } from '../help/content.ts';
import { guidesFor } from '../help/guides.ts';
import { parseMarkdown, type Block, type Inline } from '../lib/markdown.ts';
import { screensFor } from '../lib/routes.ts';
import { useApp } from '../ui/context.tsx';

/** The user guides for the signed-in user's role, in the portal's language; each one prints on its own. */
export function Help() {
  const { t, lang, user, route, go } = useApp();
  const list = useMemo(() => guidesFor(screensFor(user.role)), [user.role]);
  const current = list.find(g => g.id === route.params.g) ?? list[0];
  const blocks = useMemo(() => parseMarkdown(guideText[lang][current.id] ?? ''), [lang, current.id]);
  const titleOf = (id: string) => /^#\s+(.*)$/m.exec(guideText[lang][id] ?? '')?.[1] ?? id;

  return (
    <section className="screen help">
      <header className="screen-head no-print">
        <h1>{t('help.title')}</h1>
        <button type="button" className="btn btn-secondary" onClick={() => window.print()}>{t('help.print')}</button>
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

function BlockView({ block }: { block: Block }) {
  switch (block.kind) {
    case 'heading': {
      const H = (`h${Math.min(block.level + 1, 4)}`) as 'h2' | 'h3' | 'h4'; // the page has its own h1
      return <H id={block.id}>{block.text}</H>;
    }
    case 'paragraph':
      return <p><Inlines items={block.inline} /></p>;
    case 'note':
      return <p className="guide-note"><Inlines items={block.inline} /></p>;
    case 'list': {
      const items = block.items.map((item, i) => <li key={i}><Inlines items={item} /></li>);
      return block.ordered ? <ol>{items}</ol> : <ul>{items}</ul>;
    }
    case 'table':
      return (
        <div className="table-wrap">
          <table className="grid guide-table">
            <thead><tr>{block.head.map((c, i) => <th key={i}><Inlines items={c} /></th>)}</tr></thead>
            <tbody>
              {block.rows.map((row, r) => <tr key={r}>{row.map((c, i) => <td key={i}><Inlines items={c} /></td>)}</tr>)}
            </tbody>
          </table>
        </div>
      );
  }
}

function Inlines({ items }: { items: Inline[] }): ReactNode {
  return items.map((x, i) => {
    switch (x.kind) {
      case 'bold': return <strong key={i}>{x.text}</strong>;
      case 'code': return <code key={i} dir="ltr">{x.text}</code>;
      case 'link': return <a key={i} href={x.href} {...(x.href.startsWith('https://') ? { target: '_blank', rel: 'noreferrer' } : {})}>{x.text}</a>;
      default: return <Fragment key={i}>{x.text}</Fragment>;
    }
  });
}
