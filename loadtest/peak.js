// Yardsyncs peak-hour load test (k6). See loadtest/README.md.
//
//   BASE_URL   server to test (default http://localhost:8080) — a Development server seeded with /api/dev/load-seed
//   PROFILE    ci (default: 2 minutes, CI runner sized) or peak (10 minutes, a busy terminal; run against staging)
//
// Four kinds of traffic at the same time, as in the morning rush:
//   dispatchers  sign in once, look at availability, book import pickups into the busiest hours, cancel or move some
//   browsers     dispatchers and planners refreshing availability and the appointment list
//   trucks       drivers on the way reporting their position from the app
//   controlRoom  planners watching the control room and the gate board

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://localhost:8080';
const PROFILE = __ENV.PROFILE || 'ci';
const PASSWORD = 'Demo-Pass-2026';

const profiles = {
  ci: { dispatchers: 60, browsers: 20, trucks: 150, controlRoom: 4, duration: '2m', positionEvery: 5 },
  peak: { dispatchers: 300, browsers: 100, trucks: 1000, controlRoom: 10, duration: '10m', positionEvery: 15 },
};
const p = profiles[PROFILE];

// 409 (slot just filled, container taken) and 422 (not cleared) are answers, not failures.
http.setResponseCallback(http.expectedStatuses({ min: 200, max: 299 }, 409, 422));

export const options = {
  setupTimeout: '10m',
  scenarios: {
    dispatchers: { executor: 'constant-vus', exec: 'dispatcher', vus: p.dispatchers, duration: p.duration },
    browsers: { executor: 'constant-vus', exec: 'browser', vus: p.browsers, duration: p.duration },
    trucks: { executor: 'constant-vus', exec: 'truck', vus: p.trucks, duration: p.duration },
    controlRoom: { executor: 'constant-vus', exec: 'controlRoom', vus: p.controlRoom, duration: p.duration },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
    'http_req_duration{name:book}': ['p(95)<1500'],
    'http_req_duration{name:availability}': ['p(95)<800'],
    'http_req_duration{name:appointments}': ['p(95)<800'],
    'http_req_duration{name:position}': ['p(95)<800'],
    'http_req_duration{name:controlRoom}': ['p(95)<2000'],
    'http_req_duration{name:gateBoard}': ['p(95)<1000'],
    booked: ['count>0'],
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

const booked = new Counter('booked');
const slotTaken = new Counter('slot_taken');
const noSlotLeft = new Counter('no_slot_left');
const bookingOk = new Rate('booking_answered');

const json = { 'Content-Type': 'application/json' };
const auth = token => ({ headers: { ...json, Authorization: `Bearer ${token}` } });

function login(email) {
  const r = http.post(`${BASE}/api/auth/login`, JSON.stringify({ email, password: PASSWORD }), { headers: json, tags: { name: 'login' } });
  check(r, { 'signed in': x => x.status === 200 });
  return r.json('accessToken');
}

export function setup() {
  const seed = http.post(`${BASE}/api/dev/load-seed?companies=${p.dispatchers}&driversPerCompany=${Math.ceil(p.trucks / p.dispatchers)}`, null, { timeout: '5m' });
  if (seed.status !== 200) throw new Error(`load seed failed: ${seed.status} ${seed.body}`);
  const data = seed.json();

  const dispatchers = data.dispatchers.slice(0, p.dispatchers).map(login);
  const planner = login('planner@demo-terminal.example');

  const drivers = [];
  data.drivers.slice(0, p.trucks).forEach((d, i) => {
    const installId = `load-install-${String(i).padStart(5, '0')}`;
    const start = http.post(`${BASE}/api/driver/login/start`, JSON.stringify({ mobile: d.mobile, installId }), { headers: json, tags: { name: 'driverStart' } });
    const code = start.json('testCode');
    const verify = http.post(`${BASE}/api/driver/login/verify`, JSON.stringify({ mobile: d.mobile, installId, code, platform: 'android', appVersion: 'load' }),
      { headers: json, tags: { name: 'driverVerify' } });
    if (verify.status === 200) drivers.push({ token: verify.json('token'), appointmentNo: d.appointmentNo });
  });
  if (drivers.length < p.trucks) throw new Error(`only ${drivers.length} of ${p.trucks} drivers signed in`);
  return { terminal: data.terminalId, dispatchers, planner, drivers };
}

/** Local date in Riyadh (UTC+3), n days from today. */
function riyadhDate(days) {
  return new Date(Date.now() + 3 * 3600e3 + days * 86400e3).toISOString().slice(0, 10);
}

// ISO 6346 letter values (11, 22 and 33 are skipped).
const iso = { A: 10, B: 12, C: 13, D: 14, E: 15, F: 16, G: 17, H: 18, I: 19, J: 20, K: 21, L: 23, M: 24, N: 25, O: 26, P: 27, Q: 28, R: 29, S: 30, T: 31, U: 32, V: 34, W: 35, X: 36, Y: 37, Z: 38 };

/** A valid, unique container number per virtual user and iteration: L + two letters from the VU + U + serial + check digit. */
function containerNo(vu, iter) {
  const owner = `L${String.fromCharCode(65 + Math.floor(vu / 26) % 26)}${String.fromCharCode(65 + (vu % 26))}U`;
  const first10 = owner + String(iter % 1e6).padStart(6, '0');
  let sum = 0;
  for (let i = 0; i < 10; i++) sum += (iso[first10[i]] ?? Number(first10[i])) * (1 << i);
  return first10 + ((sum % 11) % 10);
}

function availability(data, token, date) {
  const r = http.get(`${BASE}/api/terminals/${data.terminal}/availability?date=${date}&category=IMP_PICKUP`, { ...auth(token), tags: { name: 'availability' } });
  check(r, { 'availability answered': x => x.status === 200 });
  return r.status === 200 ? r.json() : [];
}

export function dispatcher(data) {
  const token = data.dispatchers[(__VU - 1) % data.dispatchers.length];
  const date = riyadhDate(1 + (__ITER % 5));
  const now = Date.now();
  const open = availability(data, token, date).filter(s => !s.isClosed && s.available > 0 && Date.parse(s.startAt) > now + 2 * 3600e3);
  if (open.length === 0) {
    noSlotLeft.add(1);
    sleep(1);
    return;
  }
  // Everyone wants the morning: 06:00–10:00 local first.
  const morning = open.filter(s => { const h = (new Date(s.startAt).getUTCHours() + 3) % 24; return h >= 6 && h < 10; });
  const pool = morning.length > 0 && Math.random() < 0.8 ? morning : open;
  const slot = pool[Math.floor(Math.random() * Math.min(pool.length, 3))];

  const r = http.post(`${BASE}/api/terminals/${data.terminal}/appointments`,
    JSON.stringify({ slotId: slot.slotId, items: [{ category: 'IMP_PICKUP', containerNo: containerNo(__VU, __ITER) }] }),
    { ...auth(token), tags: { name: 'book' } });
  bookingOk.add(r.status === 201 || r.status === 409);
  check(r, { 'booking answered': x => x.status === 201 || x.status === 409 });
  if (r.status === 409) slotTaken.add(1);
  if (r.status === 201) {
    booked.add(1);
    const no = r.json('appointmentNo');
    const roll = Math.random();
    if (roll < 0.05) {
      const c = http.post(`${BASE}/api/terminals/${data.terminal}/appointments/${no}/cancel`, JSON.stringify({ reason: 'load test' }), { ...auth(token), tags: { name: 'cancel' } });
      check(c, { 'cancelled': x => x.status === 200 });
    } else if (roll < 0.10 && open.length > 1) {
      const other = open.find(s => s.slotId !== slot.slotId);
      const m = http.post(`${BASE}/api/terminals/${data.terminal}/appointments/${no}/reschedule`, JSON.stringify({ slotId: other.slotId }), { ...auth(token), tags: { name: 'reschedule' } });
      check(m, { 'moved or slot full': x => x.status === 200 || x.status === 409 });
    }
  }
  sleep(0.5 + Math.random());
}

export function browser(data) {
  const token = data.dispatchers[(__VU - 1) % data.dispatchers.length];
  const date = riyadhDate(1 + Math.floor(Math.random() * 5));
  availability(data, token, date);
  const r = http.get(`${BASE}/api/terminals/${data.terminal}/appointments?date=${date}`, { ...auth(token), tags: { name: 'appointments' } });
  check(r, { 'appointments answered': x => x.status === 200 });
  sleep(2 + Math.random() * 2);
}

export function truck(data) {
  const d = data.drivers[(__VU - 1) % data.drivers.length];
  // On the approach road, 15–25 km out, drifting a little each report.
  const latitude = 21.30 + ((__VU % 50) / 1000) + Math.random() / 500;
  const longitude = 39.10 + Math.random() / 500;
  const r = http.post(`${BASE}/api/driver/appointments/${d.appointmentNo}/position`,
    JSON.stringify({ latitude, longitude, speed: 14 + Math.random() * 4 }), { ...auth(d.token), tags: { name: 'position' } });
  check(r, { 'position taken': x => x.status === 200 });
  sleep(p.positionEvery * (0.8 + Math.random() * 0.4));
}

export function controlRoom(data) {
  const room = http.get(`${BASE}/api/terminals/${data.terminal}/control-room`, { ...auth(data.planner), tags: { name: 'controlRoom' } });
  check(room, { 'control room answered': x => x.status === 200 });
  const board = http.get(`${BASE}/api/terminals/${data.terminal}/gate/board`, { ...auth(data.planner), tags: { name: 'gateBoard' } });
  check(board, { 'gate board answered': x => x.status === 200 });
  sleep(3);
}
