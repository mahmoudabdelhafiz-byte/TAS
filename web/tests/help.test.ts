import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { guides, guidesFor } from '../src/help/guides.ts';
import { ar, en } from '../src/lib/i18n.ts';
import { parseInline, parseMarkdown, safeHref, slug } from '../src/lib/markdown.ts';
import { screensFor } from '../src/lib/routes.ts';

const read = (lang: string, id: string) => readFileSync(new URL(`../src/help/${lang}/${id}.md`, import.meta.url), 'utf8');

test('markdown: headings, lists, tables, notes and inline marks', () => {
  const blocks = parseMarkdown([
    '# Title', '', 'One line', 'wrapped.', '', '- **Bold** and `code`', '- a [link](#/book) and [bad](javascript:x)', '',
    '1. First', '   continued', '2. Second', '', '| A | B |', '|---|---|', '| **x** | y |', '', '> Note', '> more',
  ].join('\n'));
  assert.deepEqual(blocks.map(b => b.kind), ['heading', 'paragraph', 'list', 'list', 'table', 'note']);
  assert.deepEqual(blocks[1], { kind: 'paragraph', inline: [{ kind: 'text', text: 'One line wrapped.' }] });
  const bullets = blocks[2];
  assert.ok(bullets.kind === 'list' && !bullets.ordered);
  assert.deepEqual(bullets.items[1], [{ kind: 'text', text: 'a ' }, { kind: 'link', text: 'link', href: '#/book' }, { kind: 'text', text: ' and ' }, { kind: 'text', text: 'bad' }]);
  const ordered = blocks[3];
  assert.ok(ordered.kind === 'list' && ordered.ordered && ordered.items.length === 2);
  assert.deepEqual(ordered.items[0], [{ kind: 'text', text: 'First continued' }]);
  const table = blocks[4];
  assert.ok(table.kind === 'table' && table.rows.length === 1);
  assert.deepEqual(table.rows[0][0], [{ kind: 'bold', text: 'x' }]);
  assert.deepEqual(blocks[5], { kind: 'note', inline: [{ kind: 'text', text: 'Note more' }] });
  assert.equal(safeHref('javascript:alert(1)'), null);
  assert.equal(slug('Book one truck'), 'book-one-truck');
  assert.equal(slug('حجز شاحنة واحدة'), 'حجز-شاحنة-واحدة');
  assert.deepEqual(parseInline('plain'), [{ kind: 'text', text: 'plain' }]);
});

test('every guide exists in English and Arabic with the same sections', () => {
  for (const g of guides) {
    const [e, a] = ['en', 'ar'].map(lang => parseMarkdown(read(lang, g.id)));
    assert.equal(e[0].kind, 'heading', `${g.id} starts with its title`);
    assert.equal(a[0].kind, 'heading', `${g.id} (ar) starts with its title`);
    const shape = (blocks: typeof e) => blocks.filter(b => b.kind === 'heading').map(b => b.kind === 'heading' && b.level);
    assert.deepEqual(shape(a), shape(e), `${g.id}: same headings in both languages`);
    const tables = (blocks: typeof e) => blocks.filter(b => b.kind === 'table').map(b => b.kind === 'table' && b.rows.length);
    assert.deepEqual(tables(a), tables(e), `${g.id}: same tables in both languages`);
  }
});

test('each role gets the guides for its screens', () => {
  const ids = (role: Parameters<typeof screensFor>[0]) => guidesFor(screensFor(role)).map(g => g.id);
  assert.deepEqual(ids('CompanyUser'), ['start', 'booking', 'track', 'fleet', 'driver']);
  assert.deepEqual(ids('GateClerk'), ['start', 'booking', 'driver', 'gate', 'control']);
  assert.deepEqual(ids('TerminalAdmin'), guides.map(g => g.id));
  assert.ok(!ids('Planner').includes('admin') || screensFor('Planner').includes('map'));
});

// The guides name buttons and labels in bold: they must be the words on the screen, in each language.
const mobile = readFileSync(new URL('../../mobile/lib/strings.dart', import.meta.url), 'utf8');
const mobileTexts = (lang: string) => {
  const start = mobile.indexOf(`'${lang}': {`);
  const block = mobile.slice(start, mobile.indexOf('\n  },', start));
  return [...block.matchAll(/^\s+'[\w.]+': '((?:[^'\\]|\\.)*)',/gm)].map(m => m[1].replace(/\\'/g, "'"));
};
/** Bold words that are not labels on a screen (product names, roles in running text, permissions). */
const notLabels = new Set([
  'notifications', 'location', 'بالإشعارات', 'بالموقع', // phone permissions
  'Yardsyncs mobile for drivers', 'Yardsyncs mobile للسائقين', // a guide's title
  'English', 'العربية', // the language switch shows the other language
]);

function labels(lang: 'en' | 'ar'): string[] {
  const portal = Object.values(lang === 'en' ? en : ar) as string[];
  return [...portal, ...mobileTexts(lang)].map(s => s.replace(/\{\w+\}/g, '…'));
}

/** "Book … trucks" matches "Book {n} trucks"; a label may also be the start of a longer text. */
function onScreen(bold: string, texts: string[]): boolean {
  const pattern = new RegExp('^' + bold.split('…').map(p => p.trim().replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('.*?') + '');
  return texts.some(t => pattern.test(t.trim()));
}

for (const lang of ['en', 'ar'] as const) {
  test(`bold labels in the ${lang} guides are on the screen`, () => {
    const texts = labels(lang);
    const missing: string[] = [];
    for (const g of guides) {
      for (const block of parseMarkdown(read(lang, g.id))) {
        const inlines = block.kind === 'paragraph' || block.kind === 'note' ? [block.inline]
          : block.kind === 'list' ? block.items
          : block.kind === 'table' ? [...block.head, ...block.rows.flat()]
          : [];
        for (const inline of inlines)
          for (const x of inline)
            if (x.kind === 'bold' && !notLabels.has(x.text) && !onScreen(x.text, texts)) missing.push(`${g.id}: ${x.text}`);
      }
    }
    assert.deepEqual(missing, []);
  });
}

test('the technical pages of the public documentation exist and start with a title', async () => {
  const { techPages } = await import('../src/help/guides.ts');
  for (const id of techPages) {
    const blocks = parseMarkdown(read('tech', id));
    assert.equal(blocks[0].kind, 'heading', id);
    assert.ok(blocks.length > 5, id);
  }
});
