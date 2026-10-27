// The demo portal: the real screens, answered in the browser from sample data (mock.mjs) instead of the API.
// Nothing is saved; a page reload starts the demo again. Built by build.mjs into a folder of static files.
import { DAY, shiftDays } from './shift.js'; // first: the sample data reads the shift when it loads
import { route, useRoro } from './mock.mjs';

const DATE = /\b(20\d\d)-(\d\d)-(\d\d)(?!\d)/g;          // also inside timestamps (2026-10-15T08:00…)
const APPOINTMENT = /\bDEMO-(\d\d)(\d\d)(\d\d)-/g;           // appointment numbers carry their day: DEMO-261015-…
const move = (y, m, d, days) => new Date(Date.UTC(+y, +m - 1, +d) + days * DAY).toISOString().slice(0, 10);
const moveDates = (text, days) => days === 0 ? text : text
  .replace(DATE, (_, y, m, d) => move(y, m, d, days))
  .replace(APPOINTMENT, (_, y, m, d) => `DEMO-${move(`20${y}`, m, d, days).slice(2).replaceAll('-', '')}-`);

// ?edition=tas shows the truck appointment system alone (no Autogate screens), for customers who buy only that.
const params = new URLSearchParams(location.search);
let edition = params.get('edition');
try {
  if (edition) sessionStorage.setItem('yardsyncs.demo.edition', edition);
  else edition = sessionStorage.getItem('yardsyncs.demo.edition');
} catch { /* private window */ }
globalThis.__tasOnly = edition === 'tas';
// ?edition=roro adds vehicles (RoRo): car carriers booked by VIN, a drive-away visit and a vehicle on customs hold.
if (edition === 'roro') useRoro();

// ?zone=nafith: the terminal works next to Nafith (Egypt's economic zones): customs clearance comes from Nafith.
let zone = params.get('zone');
try {
  if (zone) sessionStorage.setItem('yardsyncs.demo.zone', zone);
  else zone = sessionStorage.getItem('yardsyncs.demo.zone');
} catch { /* private window */ }
const nafith = zone === 'nafith';
const zoneTexts = (text) => !nafith ? text : text
  .replaceAll('MSCU7654329: customs hold (inspection)', 'MSCU7654329: not cleared by customs in NAFITH (inspection pending)')
  .replaceAll('Customs hold on MSCU7654329', 'MSCU7654329: not cleared by customs in NAFITH (inspection pending)')
  .replaceAll('Customs hold (inspection)', 'Not cleared by customs in NAFITH (inspection pending)');

const notInDemo = { title: 'Not in the demo', detail: 'This action is not available in the demo. Everything else on this screen works with sample data.' };
const realFetch = window.fetch.bind(window);

window.fetch = async (input, init = {}) => {
  const url = new URL(typeof input === 'string' ? input : input.url, location.href);
  const at = url.pathname.indexOf('/api/');
  if (at < 0) return realFetch(input, init);
  const path = moveDates(decodeURIComponent(url.pathname.slice(at)), -shiftDays);
  const method = (init.method ?? 'GET').toUpperCase();
  const query = new URLSearchParams(moveDates(url.search, -shiftDays));
  let body = null;
  try { body = typeof init.body === 'string' ? JSON.parse(moveDates(init.body, -shiftDays)) : null; } catch { body = null; }

  let status, json;
  if (path === '/api/branding' || path.endsWith('/branding')) [status, json] = [404, null];        // the Yardsyncs look
  else if (path.startsWith('/api/exports/') || path.includes('/export')) [status, json] = [501, notInDemo];
  else {
    try { [status, json] = route(method, path, query, body ?? {}); } catch { [status, json] = [501, notInDemo]; }
    if (status === 404 && json?.detail?.startsWith('No mock for')) {
      (globalThis.__demoMissing ??= new Set()).add(`${method} ${path}`);
      // Saving something the sample data doesn't cover: say it worked, as the screens expect.
      [status, json] = method === 'GET' ? [501, notInDemo] : [200, { data: body ?? {}, regeneratedSlots: null }];
    }
  }
  await new Promise(r => setTimeout(r, 120));
  if (status === 204 || json === null) return new Response(null, { status: status === 404 ? 404 : 204 });
  return new Response(zoneTexts(moveDates(JSON.stringify(json), shiftDays)), { status, headers: { 'Content-Type': 'application/json' } });
};

// The demo accounts, on the sign-in page.
const accounts = [
  ['admin@demo-terminal.example', 'Terminal admin', 'مدير المحطة'],
  ['planner@demo-terminal.example', 'Planner', 'مخطط'],
  ['clerk@demo-terminal.example', 'Gate clerk', 'موظف البوابة'],
  ['dispatcher@alnoor.example', 'Trucking company', 'شركة نقل'],
  ['root@yardsyncs.example', 'System admin', 'مدير النظام'],
];

function signIn(email) {
  const set = (el, value) => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
  };
  const emailBox = document.querySelector('input[type=email]');
  const passwordBox = document.querySelector('input[type=password]');
  if (!emailBox || !passwordBox) return;
  set(emailBox, email);
  set(passwordBox, 'demo');
  document.querySelector('button[type=submit]')?.click();
}

function decorate() {
  const ar = document.documentElement.lang === 'ar';
  let bar = document.getElementById('demo-bar');
  if (!bar) {
    bar = document.createElement('div');
    bar.id = 'demo-bar';
    document.body.prepend(bar);
  }
  const barText = (ar ? 'نسخة تجريبية ببيانات افتراضية — لا يُحفظ شيء.' : 'Demo with sample data — nothing is saved.')
    + (globalThis.__tasOnly ? (ar ? ' نظام مواعيد الشاحنات فقط.' : ' Truck appointment system only.') : '')
    + (nafith ? (ar ? ' متصل بمنصة نافذ (محاكاة).' : ' Connected to Nafith (simulated).') : '');
  if (bar.textContent !== barText) bar.textContent = barText;

  const form = document.querySelector('input[type=password]')?.closest('form');
  let panel = document.getElementById('demo-accounts');
  if (!form) { panel?.remove(); return; }
  if (panel?.isConnected && panel.dataset.lang === (ar ? 'ar' : 'en')) return;
  panel?.remove();
  panel = document.createElement('div');
  panel.id = 'demo-accounts';
  panel.dataset.lang = ar ? 'ar' : 'en';
  const title = document.createElement('p');
  title.textContent = ar ? 'جرّب النظام بأحد هذه الأدوار:' : 'Try it as:';
  panel.append(title);
  for (const [email, en, arabic] of accounts) {
    const button = document.createElement('button');
    button.type = 'button';
    button.innerHTML = `<strong></strong><span></span>`;
    button.querySelector('strong').textContent = ar ? arabic : en;
    button.querySelector('span').textContent = email;
    button.addEventListener('click', () => signIn(email));
    panel.append(button);
  }
  form.after(panel);
}

const style = document.createElement('style');
style.textContent = `
#demo-bar { position: fixed; inset-inline: 0; bottom: 0; z-index: 9999; padding: 6px 16px; text-align: center;
  font: 500 13px/1.4 'IBM Plex Sans', 'IBM Plex Sans Arabic', system-ui, sans-serif; color: #fff; background: #0E7C86; }
body { padding-bottom: 30px; }
#demo-accounts { margin-top: 20px; display: grid; gap: 8px; }
#demo-accounts p { margin: 0; font-weight: 600; }
#demo-accounts button { display: flex; justify-content: space-between; gap: 12px; flex-wrap: wrap; padding: 10px 12px; text-align: start;
  border: 1px solid #C9D3DA; border-radius: 6px; background: #fff; color: #0F2A3A; font: inherit; cursor: pointer; }
#demo-accounts button:hover, #demo-accounts button:focus-visible { border-color: #0E7C86; outline: 2px solid #0E7C86; outline-offset: 1px; }
#demo-accounts span { color: #5B6B78; font-size: 13px; }
`;
document.head.append(style);
new MutationObserver(decorate).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['lang'] });
decorate();

await import('../src/main.tsx');
