// The small part of Markdown the user guides use (src/help/): headings, paragraphs, lists, tables, notes, code
// blocks, bold, code and links. Parsed into plain data and rendered as React elements, so no guide text ever becomes raw HTML.

export type Inline =
  | { kind: 'text'; text: string }
  | { kind: 'bold'; text: string }
  | { kind: 'code'; text: string }
  | { kind: 'link'; text: string; href: string };

export type Block =
  | { kind: 'heading'; level: 1 | 2 | 3; text: string; id: string }
  | { kind: 'paragraph'; inline: Inline[] }
  | { kind: 'list'; ordered: boolean; items: Inline[][] }
  | { kind: 'table'; head: Inline[][]; rows: Inline[][][] }
  | { kind: 'note'; inline: Inline[] }
  | { kind: 'code'; text: string };

/** Only links inside the portal (#/…) and to https pages. */
export function safeHref(href: string): string | null {
  return href.startsWith('#/') || href.startsWith('https://') ? href : null;
}

export function parseInline(text: string): Inline[] {
  const out: Inline[] = [];
  const pattern = /\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\(([^)\s]+)\)/g;
  let last = 0;
  for (let m = pattern.exec(text); m; m = pattern.exec(text)) {
    if (m.index > last) out.push({ kind: 'text', text: text.slice(last, m.index) });
    if (m[1] !== undefined) out.push({ kind: 'bold', text: m[1] });
    else if (m[2] !== undefined) out.push({ kind: 'code', text: m[2] });
    else {
      const href = safeHref(m[4]);
      out.push(href ? { kind: 'link', text: m[3], href } : { kind: 'text', text: m[3] });
    }
    last = m.index + m[0].length;
  }
  if (last < text.length) out.push({ kind: 'text', text: text.slice(last) });
  return out;
}

/** An id for a heading, for links within a guide: lower case, spaces to dashes, letters of any script kept. */
export function slug(text: string): string {
  return text.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-+|-+$/g, '');
}

const cells = (line: string) => line.trim().replace(/^\||\|$/g, '').split('|').map(c => parseInline(c.trim()));

export function parseMarkdown(source: string): Block[] {
  const lines = source.replace(/\r\n?/g, '\n').split('\n');
  const blocks: Block[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) { i++; continue; }

    if (line.startsWith('```')) {
      const code: string[] = [];
      i++;
      while (i < lines.length && !lines[i].startsWith('```')) code.push(lines[i++]);
      i++; // the closing fence
      blocks.push({ kind: 'code', text: code.join('\n') });
      continue;
    }

    const heading = /^(#{1,3})\s+(.*)$/.exec(line);
    if (heading) {
      const text = heading[2].trim();
      blocks.push({ kind: 'heading', level: heading[1].length as 1 | 2 | 3, text, id: slug(text) });
      i++;
      continue;
    }

    if (line.startsWith('|')) {
      const table: string[] = [];
      while (i < lines.length && lines[i].startsWith('|')) table.push(lines[i++]);
      const [head, , ...rows] = table; // the second line is |---|---|
      blocks.push({ kind: 'table', head: cells(head), rows: rows.map(cells) });
      continue;
    }

    if (line.startsWith('>')) {
      const text: string[] = [];
      while (i < lines.length && lines[i].startsWith('>')) text.push(lines[i++].replace(/^>\s?/, ''));
      blocks.push({ kind: 'note', inline: parseInline(text.join(' ')) });
      continue;
    }

    const bullet = /^\s*(?:([-*])|(\d+)\.)\s+/;
    if (bullet.test(line)) {
      const ordered = !!bullet.exec(line)![2];
      const items: string[] = [];
      while (i < lines.length && lines[i].trim()) {
        if (bullet.test(lines[i])) items.push(lines[i].replace(bullet, ''));
        else items[items.length - 1] += ' ' + lines[i].trim(); // a wrapped item
        i++;
      }
      blocks.push({ kind: 'list', ordered, items: items.map(parseInline) });
      continue;
    }

    const text: string[] = [];
    while (i < lines.length && lines[i].trim() && !/^(#|\||>|```|\s*([-*]|\d+\.)\s)/.test(lines[i])) text.push(lines[i++].trim());
    blocks.push({ kind: 'paragraph', inline: parseInline(text.join(' ')) });
  }
  return blocks;
}
