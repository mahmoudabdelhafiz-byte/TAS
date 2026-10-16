import assert from 'node:assert/strict';
import { test } from 'node:test';
import { addDictionary, en, translate } from '../src/lib/i18n.ts';
import { builtInLanguages, direction, isPortalLanguage, loadLanguages, messageLanguages, portalLanguages } from '../src/lib/languages.ts';
import { locale } from '../src/lib/time.ts';
import { appTexts, buildTemplate, readTemplate } from '../scripts/language-template.ts';

test('the translators\' template has every English text (run npm run language-template after changing texts)', () => {
  const template = readTemplate();
  assert.deepEqual(template, buildTemplate(template.messages));
  assert.equal(Object.keys(template.portal).length, Object.keys(en).length);
  assert.ok(Object.keys(appTexts('en')).length > 50);
  assert.deepEqual(Object.keys(appTexts('ar')).sort(), Object.keys(appTexts('en')).sort());
  assert.ok(template.messages.SIGN_IN_CODE.body.includes('{code}'));
});

test('a pack\'s portal texts are used, English for what it leaves out', () => {
  addDictionary('fr', { 'nav.book': 'Réserver un camion', 'nav.gate': '  ', 'not.a.key': 'x' });
  assert.equal(translate('fr', 'nav.book'), 'Réserver un camion');
  assert.equal(translate('fr', 'nav.gate'), en['nav.gate']);
  assert.equal(translate('de', 'nav.book'), en['nav.book']); // no pack at all
  assert.equal(translate('fr', 'slots.dayTotal', { b: 3, c: 9 }), translate('en', 'slots.dayTotal', { b: 3, c: 9 }));
  assert.equal(locale('fr'), 'fr-u-ca-gregory-nu-latn');
  assert.equal(locale('en'), 'en-GB');
});

test('the server\'s languages are loaded with their packs; without the server the built-in ones stay', async () => {
  const offline = await loadLanguages(async () => { throw new Error('offline'); });
  assert.deepEqual(offline, builtInLanguages);
  assert.deepEqual(portalLanguages().map(l => l.code), ['en', 'ar']);

  const asked: string[] = [];
  const list = [
    ...builtInLanguages,
    { code: 'tl', name: 'Tagalog', direction: 'ltr', portal: true, app: false, messages: true, fromPack: true },
  ];
  await loadLanguages(async url => {
    asked.push(url);
    return { ok: true, json: async () => (url === '/api/languages' ? list : { portal: { 'nav.kpi': 'Mga KPI' } }) };
  });
  assert.deepEqual(asked, ['/api/languages', '/api/languages/tl']);
  assert.equal(translate('tl', 'nav.kpi'), 'Mga KPI');
  assert.ok(isPortalLanguage('tl') && !isPortalLanguage('ur'));
  assert.deepEqual(messageLanguages().map(l => l.code), ['en', 'ar', 'ur', 'hi', 'tl']);
  assert.equal(direction('ar'), 'rtl');
  assert.equal(direction('tl'), 'ltr');
});
