// Writes deploy/languages/template.json: every English text a language pack can translate, in the pack's format.
// Portal texts come from src/lib/i18n.ts, the drivers' app's from mobile/lib/strings.dart; the messages part is kept
// as it is (the server checks it against its built-in English texts).
//   node --experimental-strip-types scripts/language-template.ts      (npm run language-template)

import { readFileSync, writeFileSync } from 'node:fs';
import { en } from '../src/lib/i18n.ts';

export const templateUrl = new URL('../../deploy/languages/template.json', import.meta.url);

/** The texts of one language in mobile/lib/strings.dart ('key': 'text', one per line). */
export function appTexts(lang: string): Record<string, string> {
  const dart = readFileSync(new URL('../../mobile/lib/strings.dart', import.meta.url), 'utf8');
  const start = dart.indexOf(`'${lang}': {`);
  const block = dart.slice(start, dart.indexOf('\n  },', start));
  return Object.fromEntries([...block.matchAll(/^\s+'([\w.]+)': '((?:[^'\\]|\\.)*)',/gm)].map(m => [m[1], m[2].replace(/\\'/g, "'")]));
}

export interface Template {
  _readme: string[];
  code: string;
  name: string;
  direction: 'ltr' | 'rtl';
  portal: Record<string, string>;
  app: Record<string, string>;
  messages: Record<string, { subject: string; body: string }>;
}

export function buildTemplate(messages: Template['messages']): Template {
  return {
    _readme: [
      'Copy this file to <code>.json (two-letter language code, e.g. fr.json) in the languages folder and translate the texts.',
      'Keep {placeholders} as they are. Leave out any text you do not translate: it shows in English.',
      'Set code, name (the language\'s own name) and direction (ltr, or rtl for Arabic-script languages). See README.md.',
    ],
    code: '',
    name: '',
    direction: 'ltr',
    portal: { ...en },
    app: appTexts('en'),
    messages,
  };
}

export function readTemplate(): Template {
  return JSON.parse(readFileSync(templateUrl, 'utf8')) as Template;
}

if (process.argv[1]?.endsWith('language-template.ts')) {
  writeFileSync(templateUrl, JSON.stringify(buildTemplate(readTemplate().messages), null, 2) + '\n');
  console.log(`Wrote ${templateUrl.pathname}`);
}
