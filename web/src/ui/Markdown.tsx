import { Fragment, type ReactNode } from 'react';
import type { Block, Inline } from '../lib/markdown.ts';

// Renders the parsed guides (lib/markdown.ts) as React elements: the text never becomes HTML.

export function BlockView({ block }: { block: Block }) {
  switch (block.kind) {
    case 'heading': {
      const H = (`h${Math.min(block.level + 1, 4)}`) as 'h2' | 'h3' | 'h4'; // the page has its own h1
      return <H id={block.id}>{block.text}</H>;
    }
    case 'paragraph':
      return <p><Inlines items={block.inline} /></p>;
    case 'code':
      return <pre className="guide-code" dir="ltr"><code>{block.text}</code></pre>;
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

export function Inlines({ items }: { items: Inline[] }): ReactNode {
  return items.map((x, i) => {
    switch (x.kind) {
      case 'bold': return <strong key={i}>{x.text}</strong>;
      case 'code': return <code key={i} dir="ltr">{x.text}</code>;
      case 'link': return <a key={i} href={x.href} {...(x.href.startsWith('https://') ? { target: '_blank', rel: 'noreferrer' } : {})}>{x.text}</a>;
      default: return <Fragment key={i}>{x.text}</Fragment>;
    }
  });
}
