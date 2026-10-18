// Sample data for the demo portal (and screenshots): the API's answers for one day at a fictional terminal (Riyadh = UTC+3).
// demo.js moves every date in these answers to the real day it is shown.
const TZ_OFFSET = 3;
export const today = '2026-10-15';
const iso = (date, hour) => {
  const [y, m, d] = date.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d, hour - TZ_OFFSET)).toISOString().replace('.000Z', '+00:00');
};
const users = {
  'dispatcher@alnoor.example': { userId: 1, email: 'dispatcher@alnoor.example', fullName: 'Khalid Al-Ghamdi', role: 'CompanyAdmin', companyId: 7, companyName: 'Al Noor Transport', terminalId: null, preferredLanguage: 'en' },
  'planner@demo-terminal.example': { userId: 3, email: 'planner@demo-terminal.example', fullName: 'Sara Al-Zahrani', role: 'Planner', companyId: null, companyName: null, terminalId: 1, preferredLanguage: 'en' },
  'admin@demo-terminal.example': { userId: 5, email: 'admin@demo-terminal.example', fullName: 'Nasser Al-Qahtani', role: 'TerminalAdmin', companyId: null, companyName: null, terminalId: 1, preferredLanguage: 'en' },
  'newadmin@demo-terminal.example': { userId: 30, email: 'newadmin@demo-terminal.example', fullName: 'Rania Haddad', role: 'TerminalAdmin', companyId: null, companyName: null, terminalId: 1, preferredLanguage: 'en', twoStepSetupRequired: true },
  'root@yardsyncs.example': { userId: 20, email: 'root@yardsyncs.example', fullName: 'Omar Siddiqui', role: 'SysAdmin', companyId: null, companyName: null, terminalId: null, preferredLanguage: 'en' },
  'clerk@demo-terminal.example': { userId: 4, email: 'clerk@demo-terminal.example', fullName: 'Faisal Qahtani', role: 'GateClerk', companyId: null, companyName: null, terminalId: 1, preferredLanguage: 'ar' },
};
const categories = [
  ['IMP_PICKUP', 'Import pickup', 'استلام حاوية وارد', 'Container', 1],
  ['EXP_DROP', 'Export drop-off', 'تسليم حاوية صادر', 'Container', 2],
  ['MTY_RETURN', 'Empty return', 'إرجاع حاوية فارغة', 'Container', 3],
  ['MTY_PICKUP', 'Empty pickup', 'استلام حاوية فارغة', 'Container', 4],
  ['BB_DELIVERY', 'Breakbulk delivery', 'استلام بضائع عامة', 'Cargo', 5],
].map(([code, nameEn, nameAr, mode, sortOrder], i) => ({ categoryId: i + 1, code, nameEn, nameAr, mode, unitType: mode === 'Cargo' ? 'Trip' : 'Move', sortOrder, isActive: true }));

const conn = { 1: 'Online', 2: 'Online', 3: 'Fault', 4: 'Online', 5: 'Offline', 7: 'Online', 8: 'Online' };
const dev = (deviceId, deviceCode, kind, reads = [], vendor = null, model = null, address = null, isActive = true) => ({ deviceId, deviceCode, kind, reads, vendor, model, address, isActive, notes: null,
  hasKey: !!conn[deviceId], connection: conn[deviceId] ?? 'NotConnected', lastSeenAt: '2026-10-15T07:52:00+00:00', statusDetail: deviceId === 3 ? 'Paper low' : null });
const lastRead = { readId: 1, deviceCode: 'IN1-CAM-FRONT', readAt: '2026-10-15T07:58:10+00:00', receivedAt: '2026-10-15T07:58:11+00:00', plate: '4410RSA', plateConfidence: 94,
  containers: [{ number: 'MSCU1234566', checkDigitOk: true, isoCode: '22G1', confidence: 90, position: 'FRONT' }, { number: 'TGHU8812340', checkDigitOk: false, isoCode: null, confidence: 61, position: 'REAR' }],
  seals: [], tag: null, appointmentNo: null, gatePassScanned: false, weightKg: null, warnings: [] };
const inspectionsOf = { appointmentNo: 'DEMO-261015-8MZ1K', status: 'GateIn', companyName: 'Al Noor Transport', truckPlate: '5678 HDR',
  items: [{ index: 1, containerNo: 'MSCU1234566', category: 'IMP_PICKUP', sealNo: 'ML449120' }],
  inspections: [
    { inspectionId: 1, stage: 'In', containerNo: 'MSCU1234566', sealExpected: 'ML449120', sealSeen: 'ML449121', sealStatus: 'Mismatch', damageFound: true, notes: null, source: 'Camera', inspectedAt: '2026-10-15T06:14:00+00:00',
      damage: [{ damageId: 1, side: 'Door', kind: 'Dent', severity: 'Minor', note: 'Small dent, right door', photoRef: 'https://ocr.example/d1.jpg', photoId: null }], photoIds: [], dispute: null },
    { inspectionId: 2, stage: 'Out', containerNo: 'MSCU1234566', sealExpected: 'ML449120', sealSeen: 'ML449121', sealStatus: 'Mismatch', damageFound: true, notes: null, source: 'Clerk', inspectedAt: '2026-10-15T07:05:00+00:00',
      damage: [{ damageId: 2, side: 'Door', kind: 'Dent', severity: 'Minor', note: null, photoRef: null, photoId: null }, { damageId: 3, side: 'Left', kind: 'Hole', severity: 'Major', note: 'About 10 cm, low', photoRef: null, photoId: null }], photoIds: [],
      dispute: { disputeId: 1, status: 'Open', reason: "The hole was made by the terminal's reach stacker.", openedAt: '2026-10-15T08:00:00+00:00', resolutionNote: null, resolvedAt: null } },
  ],
  newAtGateOut: [{ containerNo: 'MSCU1234566', side: 'Left', kind: 'Hole', severity: 'Major' }] };
const minutesAgo = m => new Date((Date.now() - (globalThis.__demoShiftMs ?? 0)) - m * 60_000).toISOString();
const clerkQueue = [
  { transactionId: 7, laneId: 1, laneCode: 'IN-1', status: 'Referred', startedAt: minutesAgo(4), lastReadAt: minutesAgo(4), decidedAt: minutesAgo(4), closedAt: null, readCount: 2, plate: '1234ABX', containers: ['MSCU1234566', 'TGHU8812340'], appointmentNo: null, gatePassScanned: false, weightKg: null, troubleId: 3, reason: 'NO_APPOINTMENT', message: 'No appointment found for plate 1234ABX.', corrected: false },
  { transactionId: 8, laneId: 1, laneCode: 'IN-1', status: 'Referred', startedAt: minutesAgo(2), lastReadAt: minutesAgo(2), decidedAt: minutesAgo(2), closedAt: null, readCount: 1, plate: '4410RSA', containers: [], appointmentNo: 'DEMO-261015-2PL7S', gatePassScanned: false, weightKg: null, troubleId: 4, reason: 'EARLY', message: 'Early: the arrival window opens at 11:30.', corrected: false },
];
const lane = (laneId, laneCode, direction, extra) => ({ lastRead: null, laneId, gateCode: 'MAIN', laneCode, direction, isActive: true, state: 'Open', mode: 'Manual', stateNote: null, stateChangedAt: null, devices: [], identifies: ['AppointmentNo', 'GatePass'], automaticProblems: [], ...extra });
const lanes = [
  lane(1, 'IN-1', 'In', { mode: 'Automatic', lastRead, transaction: { transactionId: 7, laneCode: 'IN-1', status: 'Referred', startedAt: '2026-10-15T07:58:10+00:00', lastReadAt: '2026-10-15T07:58:12+00:00', decidedAt: '2026-10-15T07:58:13+00:00', closedAt: null, readCount: 2, plate: '4410RSA', containers: ['MSCU1234566'], appointmentNo: 'DEMO-261015-2PL7S', gatePassScanned: false, weightKg: null, troubleId: 3, reason: 'EARLY', message: 'Early: the arrival window opens at 11:30.' }, identifies: ['AppointmentNo', 'GatePass', 'Plate', 'Container'], devices: [
    dev(1, 'IN1-CAM-FRONT', 'Camera', ['Plate'], 'Hikvision', 'iDS-TCM', '10.0.4.11'), dev(2, 'IN1-OCR', 'Ocr', ['Container', 'IsoCode', 'Damage'], 'Camco', 'GOS', '10.0.4.12'),
    dev(3, 'IN1-KIOSK', 'Kiosk', [], null, null, '10.0.4.15'), dev(4, 'IN1-BARRIER', 'Barrier', [], 'FAAC', 'B680H', '10.0.4.21'), dev(5, 'IN1-LIGHT', 'TrafficLight')] }),
  lane(2, 'IN-2', 'In', { state: 'OutOfOrder', stateNote: 'Barrier arm broken, technician on the way.', stateChangedAt: '2026-10-15T07:40:00+00:00',
    automaticProblems: ['NoBarrier', 'NoIdentification'] }),
  lane(3, 'IN-3', 'In', { state: 'Closed', stateNote: 'Night shift: one entry lane.', automaticProblems: ['NoBarrier', 'NoIdentification'], devices: [dev(6, 'IN3-CAM', 'Camera', ['Plate', 'Container'], null, null, null, false)] }),
  lane(4, 'OUT-1', 'Out', { mode: 'Automatic', transaction: { transactionId: 6, laneCode: 'OUT-1', status: 'Passed', startedAt: '2026-10-15T07:55:00+00:00', lastReadAt: '2026-10-15T07:55:02+00:00', decidedAt: '2026-10-15T07:55:03+00:00', closedAt: '2026-10-15T07:55:09+00:00', readCount: 1, plate: '5678HDR', containers: [], appointmentNo: 'DEMO-261015-8MZ1K', gatePassScanned: false, weightKg: null, troubleId: null, reason: null, message: 'Gated out after 52 min.' }, identifies: ['AppointmentNo', 'GatePass', 'Plate', 'Container'], devices: [dev(7, 'OUT1-CAM', 'Camera', ['Plate', 'Container']), dev(8, 'OUT1-BARRIER', 'Barrier')] }),
];
const check = (type, result, message) => ({ item: 1, type, result, message, source: 'TOS' });
const item = (category, containerNo, checks) => ({ category, containerNo, isoType: '45G1', bookingRef: null, edoRef: null, cargoRef: null, plannedTonnage: null, validationStatus: 'Pass', checks });
const appt = (no, hour, status, light, reason, plate, driver, items, company = ['Al Noor Transport', 7]) => ({
  appointmentNo: no, status, slotId: 100 + hour, slotStart: iso(today, hour), slotEnd: iso(today, hour + 1),
  slotLocal: `${today} ${String(hour).padStart(2, '0')}:00–${String(hour + 1).padStart(2, '0')}:00`,
  companyId: company[1], companyName: company[0], truckId: plate ? 1 : null, truckPlate: plate, driverId: driver ? 1 : null, driverName: driver,
  isDual: items.length > 1, trafficLight: light, trafficReason: reason, qrToken: 'x', items, createdAt: iso(today, 6),
});
const ok = [check('Availability', 'Pass', 'Discharged, in yard'), check('CustomsRelease', 'Pass', 'Customs released'), check('LineRelease', 'Pass', 'Line release received')];
export const appointments = [
  appt('DEMO-261015-4QH2D', 7, 'Completed', 'Green', 'All checks passed.', '1234 ABJ', 'Abdullah Al-Harbi', [item('IMP_PICKUP', 'MSCU1234566', ok)]),
  appt('DEMO-261015-8MZ1K', 9, 'GateIn', 'Green', 'All checks passed.', '5678 HDR', 'Mohammed Khan', [item('EXP_DROP', 'TGHU8812340', ok), item('MTY_RETURN', 'CAIU5512298', ok)]),
  appt('DEMO-261015-2PL7S', 11, 'EnRoute', 'Green', 'All checks passed.', '4410 RSA', 'Yousef Shehri', [item('IMP_PICKUP', 'MSCU5523417', ok)]),
  appt('DEMO-261015-7KQ4M', 12, 'Confirmed', 'Red', 'Customs hold on MSCU7654329.', '9021 KHB', 'Abdullah Al-Harbi',
    [item('IMP_PICKUP', 'MSCU7654329', [check('Availability', 'Pass', 'Discharged, in yard'), check('CustomsRelease', 'Fail', 'Customs hold (inspection)'), check('LineRelease', 'Pass', 'Line release received')])]),
  appt('DEMO-261015-3VB8T', 14, 'Confirmed', 'Amber', 'Line release not received yet; checked 40 min ago.', '1234 ABJ', null,
    [item('IMP_PICKUP', 'CMAU3301875', [check('Availability', 'Pass', 'Discharged, in yard'), check('CustomsRelease', 'Pass', 'Customs released'), check('LineRelease', 'Warn', 'Line release not received yet')])]),
  appt('DEMO-261015-6NX3C', 15, 'Confirmed', 'Green', 'All checks passed.', null, null, [item('MTY_PICKUP', null, [])]),
  appt('DEMO-261015-1HD9W', 16, 'Cancelled', 'Grey', 'Cancelled by dispatcher.', '5678 HDR', 'Mohammed Khan', [item('EXP_DROP', 'TGHU1029384', ok)]),
  appt('DEMO-261015-5RT2Y', 17, 'Confirmed', 'Green', 'All checks passed.', '7731 NJD', 'Omar Siddiqui', [item('EXP_DROP', 'MSKU9081726', ok)], ['Gulf Haulage', 8]),
];
appointments[5].items[0].bookingRef = 'BKG2210487';

const mix = [['IMP_PICKUP', 15], ['EXP_DROP', 12], ['MTY_RETURN', 8], ['MTY_PICKUP', 6], ['BB_DELIVERY', 5]];
const load = [0, 0, 0, 0, 0, 0, 0.35, 0.8, 0.95, 1.0, 0.9, 0.85, 0.7, 0, 0.75, 0.8, 0.65, 0.5, 0.4, 0.3, 0.2, 0.1, 0.05, 0];
export function slotBoard(date) {
  return Array.from({ length: 24 }, (_, h) => {
    const closed = h === 13;
    const total = h < 6 ? 10 : 40;
    const cats = mix.map(([category, quota], i) => {
      const booked = closed ? 0 : Math.min(quota, Math.round(quota * load[h] * (i === 4 ? 0.6 : 1) * (h === 9 && i === 0 ? 1.2 : 1)));
      return { category, quota: h < 6 ? Math.round(quota / 4) : quota, booked: h < 6 ? 0 : booked };
    });
    const totalBooked = cats.reduce((s, c) => s + c.booked, 0);
    return {
      slotId: 100 + h, startAt: iso(date, h), endAt: iso(date, h + 1), local: `${date} ${h}`,
      totalCapacity: h === 9 ? 38 : total, totalBooked: h === 9 ? 41 : totalBooked, isClosed: closed, overCapacity: h === 9, categories: cats,
    };
  });
}
export function availability(date) {
  return Array.from({ length: 17 }, (_, i) => {
    const h = 6 + i;
    const full = h === 9 || h === 10;
    return { slotId: 200 + h, startAt: iso(date, h), endAt: iso(date, h + 1), isClosed: h === 13, available: full ? 0 : Math.max(1, Math.round(15 * (1 - load[h]))) };
  });
}
export const overrides = (date) => [
  { overrideId: 11, startAt: iso(date, 13), endAt: iso(date, 14), category: null, action: 'Close', value: null, reason: 'Crane maintenance on berth 4', isActive: true, createdAt: iso(date, 5) },
  { overrideId: 12, startAt: iso(date, 15), endAt: iso(date, 17), category: 'MTY_RETURN', action: 'Adjust', value: -4, reason: 'Empty yard block C nearly full', isActive: true, createdAt: iso(date, 5) },
];
export const gateBoard = {
  atGate: [
    { appointmentNo: 'DEMO-261015-2PL7S', companyName: 'Al Noor Transport', truckPlate: '4410 RSA', status: 'Arrived', arrivedAt: null, gateInAt: null, minutesSinceArrival: 6 },
    { appointmentNo: 'DEMO-261015-9JW4E', companyName: 'Gulf Haulage', truckPlate: '3302 LMA', status: 'Arrived', arrivedAt: null, gateInAt: null, minutesSinceArrival: 14 },
  ],
  inTerminal: [
    { appointmentNo: 'DEMO-261015-8MZ1K', companyName: 'Al Noor Transport', truckPlate: '5678 HDR', status: 'GateIn', arrivedAt: null, gateInAt: new Date((Date.now() - (globalThis.__demoShiftMs ?? 0)) - 38 * 60000).toISOString(), minutesSinceArrival: 44 },
    { appointmentNo: 'DEMO-261015-0CS5N', companyName: 'Red Sea Logistics', truckPlate: '8120 TBK', status: 'GateIn', arrivedAt: null, gateInAt: new Date((Date.now() - (globalThis.__demoShiftMs ?? 0)) - 74 * 60000).toISOString(), minutesSinceArrival: 80 },
  ],
  openTroubles: 2, completedToday: 37, averageTurnMinutesToday: 52.4,
};
export const troubles = [
  { troubleId: 501, lane: 'IN-2', appointmentNo: 'DEMO-261015-7KQ4M', plateRead: '9021 KHB', containerReads: null, reason: 'NotCleared', reasonText: 'Customs hold on MSCU7654329.', status: 'Open', resolution: null, note: null, createdAt: new Date((Date.now() - (globalThis.__demoShiftMs ?? 0)) - 9 * 60000).toISOString() },
  { troubleId: 502, lane: 'IN-1', appointmentNo: null, plateRead: '6654 DKS', containerReads: null, reason: 'NoAppointment', reasonText: 'No appointment found for plate 6654 DKS.', status: 'Open', resolution: null, note: null, createdAt: new Date((Date.now() - (globalThis.__demoShiftMs ?? 0)) - 3 * 60000).toISOString() },
];

const poly = c => JSON.stringify({ type: 'Polygon', coordinates: [c] });
const box = (x0, y0, x1, y1) => poly([[x0, y0], [x1, y0], [x1, y1], [x0, y1], [x0, y0]]);
export const geofences = [
  { geofenceId: 1, name: 'Approach roads', zoneType: 'APPROACH', yardBlock: null, triggersCheckIn: false, isActive: true, geoJson: box(39.14, 21.44, 39.2, 21.5) },
  { geofenceId: 2, name: 'Pre-gate parking', zoneType: 'PRE_GATE', yardBlock: null, triggersCheckIn: true, isActive: true, geoJson: box(39.169, 21.471, 39.171, 21.4725) },
  { geofenceId: 3, name: 'Main gate', zoneType: 'GATE', yardBlock: null, triggersCheckIn: false, isActive: true, geoJson: box(39.1695, 21.47, 39.1705, 21.471) },
  { geofenceId: 4, name: 'Exit gate', zoneType: 'EXIT', yardBlock: null, triggersCheckIn: false, isActive: true, geoJson: box(39.1755, 21.4615, 39.1765, 21.4625) },
  { geofenceId: 5, name: 'Block A1', zoneType: 'YARD_ZONE', yardBlock: 'A1', triggersCheckIn: false, isActive: true, geoJson: box(39.165, 21.466, 39.168, 21.468) },
  { geofenceId: 6, name: 'Block B2', zoneType: 'YARD_ZONE', yardBlock: 'B2', triggersCheckIn: false, isActive: true, geoJson: box(39.172, 21.466, 39.175, 21.468) },
  { geofenceId: 7, name: 'Block E1', zoneType: 'YARD_ZONE', yardBlock: 'E1', triggersCheckIn: false, isActive: true, geoJson: box(39.165, 21.463, 39.168, 21.465) },
  { geofenceId: 8, name: 'Block M1', zoneType: 'YARD_ZONE', yardBlock: 'M1', triggersCheckIn: false, isActive: true, geoJson: box(39.172, 21.463, 39.175, 21.465) },
];
export const demoRoads = {"type":"FeatureCollection","features":[{"type":"Feature","properties":{"name":"Spine"},"geometry":{"type":"LineString","coordinates":[[39.17,21.47],[39.17,21.469],[39.17,21.4655],[39.17,21.4625]]}},{"type":"Feature","properties":{"name":"North road"},"geometry":{"type":"LineString","coordinates":[[39.164,21.469],[39.1665,21.469],[39.17,21.469],[39.1735,21.469],[39.176,21.469]]}},{"type":"Feature","properties":{"name":"Middle road"},"geometry":{"type":"LineString","coordinates":[[39.164,21.4655],[39.1665,21.4655],[39.17,21.4655],[39.1735,21.4655],[39.176,21.4655]]}},{"type":"Feature","properties":{"name":"South road","oneWay":true},"geometry":{"type":"LineString","coordinates":[[39.164,21.4625],[39.17,21.4625],[39.176,21.4625]]}},{"type":"Feature","properties":{"name":"West road"},"geometry":{"type":"LineString","coordinates":[[39.164,21.469],[39.164,21.4655],[39.164,21.4625]]}},{"type":"Feature","properties":{"name":"East road"},"geometry":{"type":"LineString","coordinates":[[39.176,21.469],[39.176,21.4655],[39.176,21.4625]]}}]};
export const saved = [];

const stop = (sequence, block, slot, status, arrivedAt = null, completedAt = null, stopType = 'YARD_BLOCK') => ({ sequence, stopType, block, slot, status, arrivedAt, completedAt });
const ctruck = (no, plate, company, stage, status, extra = {}) => ({
  appointmentNo: no, status, stage, company, truckPlate: plate, driverName: 'Mohammed Khan', slotStart: iso(today, 9), slotEnd: iso(today, 10),
  eta: null, arrivedAt: null, gateInAt: null, minutesInside: null, position: null, positionAt: null, moves: ['MSCU1234566'],
  stops: [], currentStop: null, overdue: false, silent: false, ...extra,
});
export const controlRoom = () => ({
  now: iso(today, 10), turnTargetMinutes: 60,
  stats: { onTheWay: 2, atGate: 1, inside: 4, completedToday: 17, avgMinutesInside: 38.4, avgMinutesToBlock: 6.2, avgMinutesAtBlock: 11.5 },
  trucks: [
    ctruck('DEMO-261015-4QH2D', '1234 ABJ', 'Al Noor Transport', 'AT_BLOCK', 'GateIn', { gateInAt: iso(today, 8), minutesInside: 72, overdue: true, position: [39.1736, 21.4671], positionAt: iso(today, 9),
      stops: [stop(1, 'B2', '14.03.2', 'ARRIVED', iso(today, 8)), stop(2, null, null, 'PENDING', null, null, 'EXIT')], currentStop: 1 }),
    ctruck('DEMO-261015-8MZ1K', '5678 HDR', 'Al Noor Transport', 'TO_BLOCK', 'GateIn', { gateInAt: iso(today, 9), minutesInside: 24, position: [39.1700, 21.4672], positionAt: iso(today, 9), moves: ['TGHU8812340', 'CAIU5512298'],
      stops: [stop(1, 'E1', null, 'DONE', iso(today, 9), iso(today, 9)), stop(2, 'M1', null, 'ACTIVE'), stop(3, null, null, 'PENDING', null, null, 'EXIT')], currentStop: 2 }),
    ctruck('DEMO-261015-2PL7S', '4410 RSA', 'Gulf Haulage', 'TO_EXIT', 'GateIn', { gateInAt: iso(today, 9), minutesInside: 31, position: [39.1760, 21.4640], positionAt: iso(today, 9),
      stops: [stop(1, 'A1', '02.11.1', 'DONE', iso(today, 9), iso(today, 9)), stop(2, null, null, 'PENDING', null, null, 'EXIT')] }),
    ctruck('DEMO-261015-5RT2Y', '7731 NJD', 'Gulf Haulage', 'TO_BLOCK', 'GateIn', { gateInAt: iso(today, 9), minutesInside: 12, silent: true, position: [39.1650, 21.4690], positionAt: iso(today, 9),
      stops: [stop(1, 'A1', '05.02.3', 'ACTIVE'), stop(2, null, null, 'PENDING', null, null, 'EXIT')], currentStop: 1 }),
    ctruck('DEMO-261015-7KQ4M', '9021 KHB', 'Al Noor Transport', 'AT_GATE', 'Arrived', { arrivedAt: iso(today, 9) }),
    ctruck('DEMO-261015-3VB8T', '1180 KSA', 'Red Sea Logistics', 'ON_THE_WAY', 'EnRoute', { eta: iso(today, 11), slotStart: iso(today, 11), slotEnd: iso(today, 12) }),
    ctruck('DEMO-261015-6NX3C', null, 'Al Noor Transport', 'ON_THE_WAY', 'EnRoute', { slotStart: iso(today, 12), slotEnd: iso(today, 13) }),
  ],
  map: { areas: geofences.filter(g => g.zoneType !== 'APPROACH').map(g => ({ name: g.name, kind: g.zoneType, block: g.yardBlock, ring: JSON.parse(g.geoJson).coordinates[0] })),
    roads: demoRoads.features.map(f => f.geometry.coordinates), bounds: [39.164, 21.4615, 39.1765, 21.4725] },
});

const chk = (type, result, message = null) => ({ type, result, message });
const cleared = [chk('Availability', 'Pass', 'Discharged, in yard'), chk('CustomsRelease', 'Pass', 'Customs released'), chk('LineRelease', 'Pass', 'Line release received'), chk('TerminalHold', 'Pass'), chk('Payment', 'Pass')];
const trk = (containerNo, isoType, sizeFt, state, checks, problems = [], warnings = [], appointment = null, bookedByOthers = false, following = false) =>
  ({ containerNo, isoType, sizeFt, state, checks, problems, warnings, appointment, bookedByOthers, following });
export const trackResult = () => ({ blNo: 'MEDUJD260001', checkedAt: iso(today, 10), containers: [
  trk('MSCU1234566', '22G1', 20, 'READY', cleared),
  trk('MSCU5523417', '22G1', 20, 'READY', cleared),
  trk('MEDU2601003', '45G1', 40, 'BOOKED', cleared, [], [], { appointmentNo: 'DEMO-261016-4QH2D', status: 'Confirmed', slotStart: iso(today, 14), slotEnd: iso(today, 15), gateOutAt: null }),
  trk('MSCU7654329', '22G1', 20, 'ON_HOLD', [chk('Availability', 'Pass'), chk('CustomsRelease', 'Fail', 'Customs hold (inspection)'), chk('LineRelease', 'Pass'), chk('TerminalHold', 'Pass'), chk('Payment', 'Pass')], ['Customs hold (inspection)'], [], null, false, true),
  trk('CMAU5100028', '42G1', 40, 'READY', [...cleared.slice(0, 4), chk('Payment', 'Warn', 'Storage charges start today')], [], ['Storage charges start today']),
  trk('TGHU4471120', '45G1', 40, 'NOT_AVAILABLE', [chk('Availability', 'Fail', 'Not discharged yet (vessel MSC AURORA, ETA 18 Oct)'), chk('CustomsRelease', 'Pass'), chk('LineRelease', 'Warn', 'Line release not received yet')], ['Not discharged yet (vessel MSC AURORA, ETA 18 Oct)'], ['Line release not received yet']),
  trk('MSCU4410022', '22G1', 20, 'BOOKED', cleared, [], [], null, true),
] });
export const watches = [
  { containerNo: 'MSCU7654329', state: 'ON_HOLD', message: 'Customs hold (inspection)', lastCheckedAt: iso(today, 10), readyNotifiedAt: null, createdAt: iso(today, 8) },
  { containerNo: 'TGHU7781203', state: 'READY', message: null, lastCheckedAt: iso(today, 10), readyNotifiedAt: iso(today, 9), createdAt: iso('2026-10-14', 8) },
  { containerNo: 'CAIU9920114', state: 'NOT_AVAILABLE', message: 'Not discharged yet', lastCheckedAt: iso(today, 10), readyNotifiedAt: null, createdAt: iso('2026-10-14', 8) },
];
export function route(method, path, query, body) {
  const date = query.get('date') ?? today;
  if (path.match(/\/messages\/templates$/)) return [200, [
    { code: 'GO_TO_TROUBLE_DESK', type: 'Template', text: 'Please come to the trouble desk at the gate.' },
    { code: 'WAIT_IN_PARKING', type: 'Template', text: 'Please wait in the truck parking. We will tell you when to come.' },
    { code: 'PROCEED_TO_GATE', type: 'Template', text: 'You can come to the gate now.' },
    { code: 'DOCUMENTS_READY', type: 'Template', text: 'Please have your ID and the delivery papers ready.' },
    { code: 'CALL_CONTROL_ROOM', type: 'Template', text: 'Please call the control room.' }]];
  if (path.match(/\/messages\/[A-Z0-9-]+$/)) {
    if (method === 'POST') return [201, { data: { messageId: 99, direction: 'ToDriver', type: 'Text', text: body.text ?? 'x', senderName: 'Huda Saleh', sentAt: new Date().toISOString(), readAt: null } }];
    return [200, { data: [
      { messageId: 1, direction: 'FromDriver', type: 'Template', templateCode: 'RUNNING_LATE', text: 'I am running late.', senderName: null, sentAt: iso(today, 9), readAt: iso(today, 9) },
      { messageId: 2, direction: 'FromDriver', type: 'Text', templateCode: null, text: 'Traffic at the King Abdullah bridge, about 20 minutes.', senderName: null, sentAt: iso(today, 9), readAt: iso(today, 9) },
      { messageId: 3, direction: 'ToDriver', type: 'Template', templateCode: 'WAIT_IN_PARKING', text: 'Please wait in the truck parking. We will tell you when to come.', senderName: 'Huda Saleh', sentAt: iso(today, 10), readAt: null },
      { messageId: 4, direction: 'FromDriver', type: 'CallRequest', templateCode: 'CALL_ME', text: 'Please call me.', senderName: null, sentAt: iso(today, 10), readAt: null }], regeneratedSlots: null }];
  }
  if (path.match(/\/messages$/)) return [200, [
    { appointmentNo: 'DEMO-261017-4KQ2M', status: 'EnRoute', slotStart: iso(today, 10), companyName: 'Al Noor Transport', truckPlate: '1234 ABJ', driverName: 'Abdullah Al-Harbi', driverMobile: '+966500000001', unread: 1,
      last: { messageId: 4, direction: 'FromDriver', type: 'CallRequest', templateCode: 'CALL_ME', text: 'Please call me.', senderName: null, sentAt: iso(today, 10), readAt: null } },
    { appointmentNo: 'DEMO-261017-9PX2D', status: 'GateIn', slotStart: iso(today, 9), companyName: 'Gulf Haulage', truckPlate: '5521 KSA', driverName: 'Mohammed Khan', driverMobile: '+966500000002', unread: 0,
      last: { messageId: 7, direction: 'ToDriver', type: 'Template', templateCode: 'PROCEED_TO_GATE', text: 'You can come to the gate now.', senderName: 'Huda Saleh', sentAt: iso(today, 8), readAt: iso(today, 8) } }]];
  if (path.endsWith('/company-report/companies')) return [200, [
    { key: '8', name: 'Gulf Haulage', appointments: 64, completed: 51, noShows: 11, cancelled: 2, noShowRate: 0.177, onTimeRate: 0.71, avgTurnMinutes: 58, avgWaitMinutes: 9 },
    { key: '7', name: 'Al Noor Transport', appointments: 122, completed: 110, noShows: 6, cancelled: 6, noShowRate: 0.05, onTimeRate: 0.88, avgTurnMinutes: 47, avgWaitMinutes: 6 },
    { key: '9', name: 'Red Sea Logistics', appointments: 31, completed: 30, noShows: 1, cancelled: 0, noShowRate: 0.03, onTimeRate: 0.93, avgTurnMinutes: 41, avgWaitMinutes: 5 }]];
  if (path.endsWith('/company-report')) {
    const days = Array.from({ length: 14 }, (_, i) => { const d = new Date(Date.parse(today) - (13 - i) * 864e5).toISOString().slice(0, 10); const n = [6,9,4,0,8,11,7,5,10,3,0,9,12,8][i]; return { date: d, capacity: 0, booked: 0, utilization: null, appointments: n, completed: Math.max(0, n - 1), noShows: n > 6 ? 1 : 0, cancelled: 0, walkIns: 0, arrived: n, onTime: n - 1, avgTurnMinutes: 45, avgWaitMinutes: 6 }; });
    return [200, { companyId: 7, companyName: 'Al Noor Transport', from: days[0].date, to: today,
      totals: { capacity: 0, booked: 0, utilization: null, appointments: 122, completed: 110, noShows: 6, cancelled: 6, walkIns: 2, noShowRate: 0.05, onTimeRate: 0.88, walkInShare: 0.016, avgTurnMinutes: 47, avgWaitMinutes: 6 },
      days,
      trucks: [
        { key: '1234 ABJ', name: '1234 ABJ', appointments: 48, completed: 45, noShows: 1, cancelled: 2, noShowRate: 0.02, onTimeRate: 0.91, avgTurnMinutes: 44, avgWaitMinutes: 5 },
        { key: '5521 KSA', name: '5521 KSA', appointments: 41, completed: 36, noShows: 4, cancelled: 1, noShowRate: 0.1, onTimeRate: 0.8, avgTurnMinutes: 53, avgWaitMinutes: 7 },
        { key: '7788 RHA', name: '7788 RHA', appointments: 33, completed: 29, noShows: 1, cancelled: 3, noShowRate: 0.03, onTimeRate: 0.9, avgTurnMinutes: 45, avgWaitMinutes: 6 }],
      drivers: [
        { key: '1', name: 'Abdullah Al-Harbi', appointments: 70, completed: 66, noShows: 2, cancelled: 2, noShowRate: 0.03, onTimeRate: 0.9, avgTurnMinutes: 44, avgWaitMinutes: 5 },
        { key: '2', name: 'Mohammed Khan', appointments: 52, completed: 44, noShows: 4, cancelled: 4, noShowRate: 0.08, onTimeRate: 0.84, avgTurnMinutes: 51, avgWaitMinutes: 7 }],
      holds: [
        { check: 'CustomsRelease', visits: 7, lastReason: 'Customs hold on MSCU7654329' },
        { check: 'Payment', visits: 3, lastReason: 'Charges for EDO-77 are unpaid' }],
      charges: [{ currency: 'SAR', charges: 9, amountMinor: 27500, noShows: 6, lateCancels: 1 }] }];
  }
  if (path.match(/\/driver-records\/\d+$/)) return [200, { driverId: 1, fullName: 'Abdullah Al-Harbi', mobile: '+966500000001', companyName: 'Al Noor Transport', points: 7, standing: 'Warning', warnPoints: 6, limitPoints: 0, pointsDays: 180,
    incidents: [
      { incidentId: 3, appointmentNo: 'DEMO-261017-4KQ2M', kind: 'Speeding', points: 3, occurredAt: iso(today, 9), note: '45 km/h on the quay road', recordedByName: 'Huda Saleh', disputeStatus: 'Open', disputeText: 'I was at 25 km/h, the camera is wrong', disputeAnswer: null, withdrawnAt: null, withdrawReason: null, counts: true, countsUntil: iso(today, 9) },
      { incidentId: 2, appointmentNo: null, kind: 'NoPpe', points: 2, occurredAt: iso(today, 8), note: 'No vest at block B2', recordedByName: 'Huda Saleh', disputeStatus: null, disputeText: null, disputeAnswer: null, withdrawnAt: null, withdrawReason: null, counts: true, countsUntil: iso(today, 8) },
      { incidentId: 1, appointmentNo: null, kind: 'WrongArea', points: 2, occurredAt: iso(today, 7), note: null, recordedByName: 'Huda Saleh', disputeStatus: null, disputeText: null, disputeAnswer: null, withdrawnAt: iso(today, 8), withdrawReason: 'Wrong driver', counts: false, countsUntil: iso(today, 7) }] }];
  if (path.endsWith('/driver-records')) return [200, [
    { driverId: 1, fullName: 'Abdullah Al-Harbi', mobile: '+966500000001', companyName: 'Al Noor Transport', points: 7, standing: 'Warning', open: 1, lastAt: iso(today, 9) },
    { driverId: 4, fullName: 'Imran Siddiqui', mobile: '+966500000014', companyName: 'Gulf Haulage', points: 12, standing: 'OverLimit', open: 0, lastAt: iso(today, 7) },
    { driverId: 2, fullName: 'Mohammed Khan', mobile: '+966500000002', companyName: 'Al Noor Transport', points: 2, standing: 'Clear', open: 0, lastAt: iso(today, 6) }]];
  if (path === '/api/auth/two-step/setup') return [200, { data: { secret: 'JBSW Y3DP EHPK 3PXP JBSW Y3DP EHPK 3PXP', uri: 'otpauth://totp/Yardsyncs:newadmin%40demo-terminal.example?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Yardsyncs&digits=6&period=30' }, regeneratedSlots: null }];
  if (path.endsWith('/appointments/search')) {
    const all = route('GET', path.replace('/search', ''), new URLSearchParams(`date=${today}`), null)[1];
    return [200, all.slice(0, 3)];
  }
  if (path === '/api/auth/login') {
    const user = users[body.email];
    if (body.email === 'twostep@demo-terminal.example') return [200, { secondStep: true, challenge: 'c' }];
    return user ? [200, { accessToken: 't', refreshToken: 'r', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user }] : [401, { detail: 'Wrong email or password.' }];
  }
  if (path === '/api/auth/forgot-password') return [202, { sent: true }];
  if (path === '/api/assistant') return [200, { enabled: true, liveData: true, dailyQuestions: 60 }];
  if (path.endsWith('/assistant')) {
    const ar = body.lang === 'ar';
    return [200, { questionsLeft: 57, lookedUp: ['list_appointments'], text: ar
      ? 'لديك **3 مواعيد** غداً، واحد منها **موقوفة**:\n\n| الموعد | الساعة | السبب |\n|---|---|---|\n| `DEMO-261017-4KQ2M` | 10:00 | إيقاف جمركي على MSCU7654329 |\n\nلا ترسل الشاحنة حتى يُفرج عنها. افتح [المواعيد](#/appointments) واضغط **أعد التحقق الآن** بعد الإفراج.'
      : 'You have **3 appointments** tomorrow; one is **On hold**:\n\n| Appointment | Hour | Why |\n|---|---|---|\n| `DEMO-261017-4KQ2M` | 10:00 | Customs hold on MSCU7654329 |\n\nDon\'t send that truck until it is released. Open [Appointments](#/appointments) and press **Re-check now** after the release.' }];
  }
  if (path === '/api/auth/password-link' && method === 'GET') return query.get('token') === 'bad' ? [404, { detail: 'gone' }] : [200, { purpose: 'INVITE', fullName: 'Huda Saleh', email: 'huda.saleh@demo-terminal.example', preferredLanguage: 'en' }];
  if (path === '/api/auth/password-link') return [204, null];
  if (path.endsWith('/password-link')) return [202, null];
  if (path.endsWith('/holidays')) return [200, [{ date: '2026-11-05', nameEn: 'National holiday', nameAr: 'عطلة وطنية', lunar: false }]];
  if (path.endsWith('/fees')) return [200, { currency: 'SAR', decimals: 2, booking: 2500, noShow: 10000, lateCancel: 5000, lateCancelHours: 24 }];
  if (path.endsWith('/charges')) return [200, { currency: 'SAR', decimals: 2, totalMinor: 37500, companies: [
      { companyId: 7, companyName: 'Al Noor Transport', charges: 9, amountMinor: 27500 }, { companyId: 8, companyName: 'Gulf Haulage', charges: 2, amountMinor: 10000 }],
    charges: [
      { chargeId: 1, appointmentNo: 'DEMO-261015-7KQ4M', companyId: 7, companyName: 'Al Noor Transport', kind: 'Booking', amountMinor: 2500, currency: 'SAR', createdAt: iso(today, 8), waivedAt: null, waiveReason: null },
      { chargeId: 2, appointmentNo: 'DEMO-261015-9PX2D', companyId: 8, companyName: 'Gulf Haulage', kind: 'NoShow', amountMinor: 10000, currency: 'SAR', createdAt: iso(today, 9), waivedAt: null, waiveReason: null },
      { chargeId: 3, appointmentNo: 'DEMO-261015-3HT8C', companyId: 7, companyName: 'Al Noor Transport', kind: 'LateCancel', amountMinor: 5000, currency: 'SAR', createdAt: iso(today, 10), waivedAt: iso(today, 11), waiveReason: 'Crane breakdown, our fault' },
    ] }];
  if (path.endsWith('/setup')) return [200, { terminalId: 1, code: 'DEMO', readyToBook: false, adapter: 'Fake', tosConnected: true, steps: [
    { key: 'moveTypes', done: true, optional: false, detail: '', count: 5, count2: null },
    { key: 'weeklyPlan', done: true, optional: false, detail: '', count: null, count2: null },
    { key: 'slots', done: false, optional: false, detail: '', count: 0, count2: null },
    { key: 'lanes', done: true, optional: false, detail: '', count: 2, count2: 1 },
    { key: 'tos', done: false, optional: false, detail: '', count: null, count2: null },
    { key: 'staff', done: true, optional: false, detail: '', count: 3, count2: null },
    { key: 'companies', done: true, optional: false, detail: '', count: 2, count2: null },
    { key: 'map', done: false, optional: true, detail: '', count: 0, count2: null },
  ] }];
  if (path === '/api/branding') return [200, { name: 'PortOps', color: '#1f4e79', logoUrl: null, operatorId: null }];
  if (path.endsWith('/branding') && path.startsWith('/api/terminals/')) return [200, { name: 'Harbor One', color: '#7a1f3d', logoUrl: null, operatorId: 1 }];
  if (path === '/api/branding/installation') return [200, { operatorId: null, name: 'PortOps', color: '#1f4e79', domain: null, logoUrl: null }];
  if (path.endsWith('/branding') && path.startsWith('/api/operators/')) return [200, { operatorId: 1, name: 'Harbor One', color: '#7a1f3d', domain: 'tas.harbor-one.example', logoUrl: null }];
  if (path === '/api/languages') return [200, [
    { code: 'en', name: 'English', direction: 'ltr', portal: true, app: true, messages: true, fromPack: false },
    { code: 'ar', name: 'العربية', direction: 'rtl', portal: true, app: true, messages: true, fromPack: false },
    { code: 'ur', name: 'اردو', direction: 'rtl', portal: false, app: true, messages: true, fromPack: false },
    { code: 'hi', name: 'हिन्दी', direction: 'ltr', portal: false, app: true, messages: true, fromPack: false },
    { code: 'fr', name: 'Français', direction: 'ltr', portal: true, app: true, messages: true, fromPack: true },
  ]];
  if (path === '/api/languages/fr') return [200, { code: 'fr', name: 'Français', direction: 'ltr', app: {}, portal: {
    'nav.book': 'Réserver un camion', 'nav.appointments': 'Rendez-vous', 'nav.slots': 'Créneaux', 'nav.help': 'Aide', 'nav.fleet': 'Flotte et utilisateurs',
    'book.title': 'Réserver un camion', 'login.submit': 'Se connecter', 'login.email': 'E-mail', 'login.password': 'Mot de passe', 'login.signOut': 'Se déconnecter' } }];
  if (path === '/api/country') return [200, { code: 'SA', nameEn: 'Saudi Arabia', nameAr: 'المملكة العربية السعودية', dialCode: '+966', mobileExample: '05XXXXXXXX', idNameEn: 'ID / Iqama number', idNameAr: 'رقم الهوية / الإقامة', currency: 'SAR', currencyDecimals: 2, timeZone: 'Asia/Riyadh' }];
  if (path === '/api/operators') return [200, [
    { operatorId: 1, code: 'HARBOR1', name: 'Harbor One Terminals', isActive: true, terminals: [{ terminalId: 1, code: 'DEMO', name: 'Demo Container Terminal' }, { terminalId: 2, code: 'NORTH', name: 'North Quay Terminal' }], admins: 2 },
    { operatorId: 2, code: 'BAYPORT', name: 'Bayport Logistics', isActive: false, terminals: [], admins: 0 },
  ]];
  if (path === '/api/users' && query.get('operatorId')) return [200, [
    { userId: 21, email: 'ops.head@harbor-one.example', fullName: 'Hana Barakat', role: 'OperatorAdmin', companyId: null, terminalId: null, mobile: null, preferredLanguage: 'en', isActive: true, lastLoginAt: iso(today, 8) },
    { userId: 22, email: 'deputy@harbor-one.example', fullName: 'Yousef Malik', role: 'OperatorAdmin', companyId: null, terminalId: null, mobile: null, preferredLanguage: 'ar', isActive: true, lastLoginAt: null },
  ]];
  if (path === '/api/terminals') return [200, [{ terminalId: 1, code: 'DEMO', name: 'Demo Container Terminal', terminalType: 'Container', timeZone: 'Asia/Riyadh', slotMinutes: 60, bookingHorizonDays: 14, autogate: !globalThis.__tasOnly }]];
  if (path.endsWith('/containers/track')) return [200, { data: trackResult(), regeneratedSlots: null }];
  if (path.endsWith('/containers/followed') && method === 'GET') return [200, watches];
  if (path.includes('/containers/followed')) return [method === 'DELETE' ? 204 : 200, method === 'DELETE' ? null : { data: watches, regeneratedSlots: null }];
  if (path.endsWith('/control-room')) return [200, controlRoom()];
  if (path.endsWith('/geofences') && method === 'GET') return [200, geofences];
  if (path.endsWith('/yard-map/roads') && method === 'GET') return [200, demoRoads];
  if (path.includes('/geofences') || path.endsWith('/yard-map/roads')) { saved.push([method, path, body]); return method === 'POST' && path.endsWith('/geofences') ? [201, { geofenceId: 99 }] : [204, null]; }
  if (path.endsWith('/categories')) return [200, categories];
  if (path === '/api/companies') return [200, [
    { companyId: 7, name: 'Al Noor Transport', status: 'Active', crNumber: '4030123456', companyType: 'Trucking', isPriority: false, noShowCount: 3 },
    { companyId: 8, name: 'Gulf Haulage', status: 'Active', crNumber: '4030654321', companyType: 'Trucking', isPriority: false, noShowCount: 11 },
    { companyId: 9, name: 'Red Sea Logistics', status: 'Suspended', crNumber: '4030999999', companyType: 'Forwarder', isPriority: false, noShowCount: 0 }]];
  if (path.endsWith('/trucks')) return [200, [{ truckId: 1, companyId: 7, plateNumber: '1234 ABJ', isBlocked: false }, { truckId: 2, companyId: 7, plateNumber: '5678 HDR', isBlocked: false }, { truckId: 3, companyId: 7, plateNumber: '9021 KHB', isBlocked: true }]];
  if (path.endsWith('/drivers')) return [200, [{ driverId: 1, companyId: 7, fullName: 'Abdullah Al-Harbi', idNumber: '1012345678', mobile: '+966500000001', preferredLanguage: 'ar', isBlocked: false }, { driverId: 2, companyId: 7, fullName: 'Mohammed Khan', idNumber: 'ERASED-2', mobile: '', preferredLanguage: 'ur', isBlocked: false }]];
  if (path.endsWith('/availability')) return [200, availability(date)];
  if (path.endsWith('/capacity/slot-board')) return [200, slotBoard(date)];
  if (path.endsWith('/capacity/overrides') && method === 'GET') return [200, overrides(date)];
  if (path.endsWith('/gate/board')) return [200, gateBoard];
  if (path.endsWith('/gate/troubles')) return [200, troubles];
  if (path.endsWith('/gate/systems') && method === 'GET') return [200, [
    { keyId: 1, name: 'North gate OCR portal', keyHint: '…k3Pq9Z', createdAt: iso(today, 7), lastUsedAt: iso(today, 9), revokedAt: null },
    { keyId: 2, name: 'Pre-gate kiosks', keyHint: '…Tm2w8a', createdAt: iso('2026-10-14', 10), lastUsedAt: null, revokedAt: iso('2026-10-16', 12) },
  ]];
  if (path.endsWith('/gate/systems') && method === 'POST') return [201, { data: { system: { keyId: 3, name: body.name, keyHint: '…Xa91Lk', createdAt: iso(today, 10), lastUsedAt: null, revokedAt: null }, key: 'ysg_5Gf0q2V0c1yQe7mHn4rT8uWz3bLk6dJpS9aXa91Lk' }, regeneratedSlots: null }];
  if (path.endsWith('/gate/lane-kpis')) {
    const k = (trucks, st, ex, ml, md, pr, cr, ab = 0, p90 = ml * 3) => ({ trucks, abandoned: ab, straightThroughRate: st, exceptionRate: ex, medianDecisionSeconds: md, medianLaneSeconds: ml, p90LaneSeconds: p90, plateReadRate: pr, containerReadRate: cr });
    const from = query.get('from'); const days = [];
    for (let i = 0; i < 7; i++) { const d = new Date(from + 'T12:00:00Z'); d.setUTCDate(d.getUTCDate() + i); days.push({ date: d.toISOString().slice(0, 10), trucks: 300 + i * 17, exceptionRate: [0.12, 0.09, 0.14, 0.08, 0.11, 0.07, 0.1][i], medianLaneSeconds: 48 + i }); }
    return [200, {
      from, to: query.get('to'),
      totals: k(2271, 0.896, 0.104, 52, 9, 0.962, 0.931, 41, 186),
      lanes: [
        { laneId: 1, laneCode: 'IN-1', direction: 'In', mode: 'Automatic', kpis: k(1012, 0.912, 0.088, 47, 8, 0.971, 0.944, 18, 160) },
        { laneId: 2, laneCode: 'IN-2', direction: 'In', mode: 'Automatic', kpis: k(698, 0.861, 0.139, 58, 11, 0.938, 0.902, 15, 230) },
        { laneId: 4, laneCode: 'OUT-1', direction: 'Out', mode: 'Automatic', kpis: k(561, 0.905, 0.095, 41, 7, 0.975, null, 8, 140) },
      ],
      days,
      reasons: [{ reason: 'EARLY', count: 96, share: 0.406 }, { reason: 'NO_APPOINTMENT', count: 54, share: 0.229 }, { reason: 'EXIT_CHECK', count: 38, share: 0.161 }, { reason: 'NOT_CLEARED', count: 31, share: 0.131 }, { reason: 'BARRIER', count: 17, share: 0.072 }],
    }];
  }
  if (path.endsWith('/gate/lanes')) return [200, lanes];
  if (/\/appointments\/[^/]+\/inspections$/.test(path)) return [200, inspectionsOf];
  if (path.endsWith('/inspections')) return [200, [
    { inspectionId: 2, appointmentNo: 'DEMO-261015-8MZ1K', companyName: 'Al Noor Transport', containerNo: 'MSCU1234566', stage: 'Out', inspectedAt: '2026-10-15T07:05:00+00:00', source: 'Clerk', sealStatus: 'Ok', damageCount: 2, dispute: 'Open', disputeId: 1 },
    { inspectionId: 1, appointmentNo: 'DEMO-261015-8MZ1K', companyName: 'Al Noor Transport', containerNo: 'MSCU1234566', stage: 'In', inspectedAt: '2026-10-15T06:14:00+00:00', source: 'Camera', sealStatus: 'Mismatch', damageCount: 1, dispute: null, disputeId: null },
    { inspectionId: 3, appointmentNo: 'DEMO-261015-0CS5N', companyName: 'Red Sea Logistics', containerNo: 'TGHU8812340', stage: 'In', inspectedAt: '2026-10-15T06:50:00+00:00', source: 'Clerk', sealStatus: 'Broken', damageCount: 0, dispute: null, disputeId: null },
  ]];
  if (/\/gate\/tickets\//.test(path)) return [200, { terminalName: 'Demo Container Terminal', appointmentNo: 'DEMO-261015-8MZ1K', status: 'Completed', companyName: 'Al Noor Transport',
    truckPlate: '5678 HDR', driverName: 'Faisal Al-Harbi', slotLocal: '2026-10-15 09:00–10:00', arrivedAt: '2026-10-15T06:12:00+00:00', gateInAt: '2026-10-15T06:14:00+00:00', gateOutAt: '2026-10-15T07:06:00+00:00',
    inLane: 'IN-1', outLane: 'OUT-1', tosVisitReference: '881234', weightKg: 31250,
    moves: [{ category: 'Import pickup', categoryAr: 'استلام حاوية وارد', containerNo: 'MSCU1234566', isoType: '22G1', reference: 'EDO-55120', seals: ['ML-449120'] }],
    damage: [{ photo: null, note: 'Dent, left door', side: 'REAR' }] }];
  if (path.endsWith('/gate/tos-records')) return [200, [
    { messageId: 3, appointmentNo: 'DEMO-261015-8MZ1K', stage: 'GateOut', laneCode: 'OUT-1', occurredAt: '2026-10-15T07:06:00+00:00', status: 'Queued', attempts: 2, nextAttemptAt: '2026-10-15T07:10:00+00:00', sentAt: null, tosReference: null, lastError: 'N4 did not answer within 15 s.' },
    { messageId: 2, appointmentNo: 'DEMO-261015-0CS5N', stage: 'GateIn', laneCode: 'IN-2', occurredAt: '2026-10-15T06:50:00+00:00', status: 'Failed', attempts: 1, nextAttemptAt: '2026-10-15T06:50:00+00:00', sentAt: null, tosReference: null, lastError: 'N4: Unit TGHU8812340 has an active hold' },
    { messageId: 1, appointmentNo: 'DEMO-261015-8MZ1K', stage: 'GateIn', laneCode: 'IN-1', occurredAt: '2026-10-15T06:14:00+00:00', status: 'Sent', attempts: 1, nextAttemptAt: '2026-10-15T06:14:00+00:00', sentAt: '2026-10-15T06:14:05+00:00', tosReference: '881234', lastError: null },
  ]];
  if (/\/gate\/transactions\/\d+$/.test(path)) {
    const tx = clerkQueue.find(x => path.endsWith(`/${x.transactionId}`)) ?? clerkQueue[0];
    return [200, { transaction: tx, devices: lanes[0].devices,
      reads: [
        { ...lastRead, readId: 11, plate: tx.plate, plateConfidence: 58, containers: [], photos: ['https://picsum.photos/id/1071/320/200', 'https://picsum.photos/id/1072/320/200'], damage: [] },
        { ...lastRead, readId: 12, deviceCode: 'IN1-OCR', plate: null, plateConfidence: null, photos: [], damage: [{ photo: 'ocr://cam2/77812.jpg', note: 'Dent, rear door', side: 'REAR' }] },
      ],
      commands: [], trouble: null }];
  }
  if (path.endsWith('/gate/transactions')) return [200, clerkQueue];
  if (/\/devices\/\d+\/key$/.test(path)) return [201, { data: { deviceCode: 'IN1-OCR', key: 'ysd_q8Hk2vR0bX9mLwTzN4sPj7cYe1uDfAg6' } }];
  if (path.endsWith('/gate/arrivals')) return [200, { data: { decision: 'Refer', message: 'Customs hold on MSCU7654329. Sent to the trouble desk.', appointmentNo: 'DEMO-261015-7KQ4M', matchedBy: 'Plate', light: 'Red', troubleId: 503, reason: 'NotCleared', warnings: [] }, regeneratedSlots: null }];
  const m = path.match(/\/appointments\/([^/]+)(\/notifications)?$/);
  if (m && method === 'GET') {
    if (m[2]) return [200, [
      { template: 'STATUS_RED', channel: 'Sms', language: 'ar', status: 'Sent', recipient: '+9665…01', attempts: 1, lastError: null, createdAt: iso(today, 8), sentAt: iso(today, 8) },
      { template: 'STATUS_RED', channel: 'Email', language: 'en', status: 'Sent', recipient: 'dispatcher@…', attempts: 1, lastError: null, createdAt: iso(today, 8), sentAt: iso(today, 8) },
    ]];
    return [200, appointments.find(a => a.appointmentNo === m[1]) ?? appointments[3]];
  }
  if (path.endsWith('/appointments') && method === 'GET') {
    const company = query.get('companyId');
    return [200, appointments.filter(a => !company || String(a.companyId) === company)];
  }
  if (path.endsWith('/capacity/templates') && method === 'GET') {
    const week = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday'];
    const q = { IMP_PICKUP: 15, EXP_DROP: 12, MTY_RETURN: 8, MTY_PICKUP: 6, BB_DELIVERY: 5 };
    const windows = [
      ...week.flatMap(day => [
        { day, start: '00:00', end: '06:00', totalCapacity: 10, overbookPct: 0, quotas: { BB_DELIVERY: 0 } },
        { day, start: '06:00', end: '13:00', totalCapacity: 40, overbookPct: 5, quotas: q },
        { day, start: '14:00', end: '22:00', totalCapacity: 40, overbookPct: 5, quotas: q },
        { day, start: '22:00', end: '24:00', totalCapacity: 20, overbookPct: 0, quotas: {} },
      ]),
      ...['Friday', 'Saturday'].map(day => ({ day, start: '08:00', end: '20:00', totalCapacity: 25, overbookPct: 0, quotas: { BB_DELIVERY: 0 } })),
    ];
    return [200, [
      { templateId: 1, name: 'Standard week', isDefault: true, validFrom: null, validTo: null, priority: 0, isActive: true, windows },
      { templateId: 2, name: 'Ramadan 2027', isDefault: false, validFrom: '2027-02-08', validTo: '2027-03-09', priority: 10, isActive: true,
        windows: week.map(day => ({ day, start: '09:00', end: '16:00', totalCapacity: 30, overbookPct: 0, quotas: {} })) },
    ]];
  }
  if (path.includes('/capacity/templates') && (method === 'PUT' || method === 'POST'))
    return [200, { data: { templateId: 1, ...body }, regeneratedSlots: { created: 0, updated: 312, closed: 14, overCapacity: 2 } }];
  if (path.endsWith('/capacity/reservations') && method === 'GET') return [200, [
    { reservationId: 1, companyId: null, category: null, startAt: iso(today, 6), endAt: iso('2026-12-31', 22), unitsPerSlot: 2, releaseHoursBefore: 2, isActive: true },
    { reservationId: 2, companyId: 7, category: 'IMP_PICKUP', startAt: iso('2026-10-18', 6), endAt: iso('2026-11-18', 14), unitsPerSlot: 4, releaseHoursBefore: 12, isActive: true },
  ]];
  if (path.endsWith('/capacity/release-rules')) return [200, method === 'GET' ? [{ category: 'MTY_RETURN', hoursBefore: 12 }] : { data: [], regeneratedSlots: null }];
  if (path.endsWith('/settings')) return [200, method === 'GET' ? { early_grace_min: 30, late_grace_min: 30, cancel_cutoff_min: 60, max_appt_per_truck_per_day: 6, report_hour: 6, report_weekday: 0, induction_mode: 1, slot_advice: 1, slot_target_wait: 20, slot_max_overbook: 15, slot_history_weeks: 8, driver_points_days: 90, driver_warn_points: 6, driver_limit_points: 10, driver_record_company: 1 } : { data: {}, regeneratedSlots: null }];
  if (path.endsWith('/appointments/bulk')) {
    const dry = query.get('dryRun') === 'true';
    const rows = [
      { visit: 'V1', rows: [2, 3], slot: '2026-10-16 08:00', status: dry ? 'WouldBook' : 'Booked', appointmentNo: dry ? null : 'DEMO-261016-3RT8K', errors: [], checks: [] },
      { visit: 'V2', rows: [4], slot: '2026-10-16 08:00', status: dry ? 'WouldBook' : 'Booked', appointmentNo: dry ? null : 'DEMO-261016-8PL2D', errors: [], checks: [] },
      { visit: 'V3', rows: [5], slot: '2026-10-16 09:00', status: 'Failed', appointmentNo: null, errors: ['No capacity left for Import pickup at 09:00.'], checks: [] },
      { visit: 'V4', rows: [6], slot: '2026-10-16 10:00', status: 'Failed', appointmentNo: null, errors: [], checks: [check('CustomsRelease', 'Fail', 'Customs hold on MSCU7654329')] },
      { visit: 'V5', rows: [7, 8], slot: '2026-10-16 14:00', status: dry ? 'WouldBook' : 'Booked', appointmentNo: dry ? null : 'DEMO-261016-1ZX9Q', errors: [], checks: [] },
    ];
    return [200, { dryRun: dry, visits: 5, succeeded: 3, failed: 2, fileErrors: [], results: rows }];
  }
  if (path.endsWith('/kpis')) {
    const from = query.get('from'), to = query.get('to');
    const days = []; let d = from;
    const add = (x, n) => { const [y, m, dd] = x.split('-').map(Number); return new Date(Date.UTC(y, m - 1, dd + n)).toISOString().slice(0, 10); };
    let i = 0;
    while (d <= to) {
      const cap = [5, 6].includes(new Date(d).getUTCDay()) ? 300 : 620;
      const booked = Math.round(cap * (0.62 + 0.25 * Math.sin(i * 1.3) * Math.sin(i * 0.4) + (i % 3) * 0.04));
      const appts = Math.round(booked * 0.7);
      days.push({ date: d, capacity: cap, booked, utilization: booked / cap, appointments: appts, completed: Math.round(appts * 0.88), noShows: Math.round(appts * 0.06),
        cancelled: Math.round(appts * 0.05), walkIns: Math.round(appts * 0.03), arrived: Math.round(appts * 0.92), onTime: Math.round(appts * 0.8),
        avgTurnMinutes: 44 + 14 * Math.sin(i * 0.9) + (i % 4) * 3, avgWaitMinutes: 8 + (i % 5) });
      d = add(d, 1); i++;
    }
    const hours = Array.from({ length: 24 }, (_, h) => ({ hour: h, capacity: h < 6 ? 70 : 280, booked: Math.round((h < 6 ? 70 : 280) * (load[h] || 0.08)), utilization: load[h] || 0.08, appointments: 0, avgTurnMinutes: null }));
    const sum = k => days.reduce((a, x) => a + x[k], 0);
    return [200, { from, to, days, hours, totals: { capacity: sum('capacity'), booked: sum('booked'), utilization: sum('booked') / sum('capacity'), appointments: sum('appointments'),
      completed: sum('completed'), noShows: sum('noShows'), cancelled: sum('cancelled'), walkIns: sum('walkIns'), noShowRate: 0.061, onTimeRate: 0.87, walkInShare: 0.031,
      avgTurnMinutes: 47.6, avgWaitMinutes: 9.2 },
      yard: { visits: 1240, stops: 1810, appSeenShare: 0.83, avgToBlockMinutes: 6.4, avgAtBlockMinutes: 14.2, avgToExitMinutes: 5.1,
        days: days.map((x, j) => ({ date: x.date, visits: Math.round(x.completed * 0.9), avgToBlockMinutes: 6 + (j % 3), avgAtBlockMinutes: 12 + 6 * Math.abs(Math.sin(j * 0.8)) })),
        blocks: [['B2', 412, 7.8, 21.4], ['A1', 380, 6.1, 15.9], ['M1', 318, 4.9, 12.2], ['E1', 501, 5.2, 9.6], ['C07', 199, 8.4, 8.8]]
          .map(([block, stops, avgDriveMinutes, avgAtBlockMinutes]) => ({ block, stops, avgDriveMinutes, avgAtBlockMinutes })) } }];
  }
  if (path === '/api/users') return [200, [
    { userId: 1, email: 'dispatcher@alnoor.example', fullName: 'Khalid Al-Ghamdi', role: 'CompanyAdmin', companyId: 7, terminalId: null, mobile: null, preferredLanguage: 'en', isActive: true, lastLoginAt: iso(today, 9) },
    { userId: 9, email: 'night.desk@alnoor.example', fullName: 'Fahad Otaibi', role: 'CompanyUser', companyId: 7, terminalId: null, mobile: null, preferredLanguage: 'ar', isActive: true, lastLoginAt: iso('2026-10-14', 23) },
    { userId: 10, email: 'old.clerk@alnoor.example', fullName: 'Sami Harbi', role: 'CompanyUser', companyId: 7, terminalId: null, mobile: null, preferredLanguage: 'ar', isActive: false, lastLoginAt: null },
  ]];
  if (path.endsWith('/containers/lookup')) {
    const row = (containerNo, isoType, status, problems = [], warnings = [], appointmentNo = null) =>
      ({ containerNo, isoType, sizeFt: isoType ? ({ '2': 20, '4': 40, 'L': 45 })[isoType[0]] ?? null : null, status, problems, warnings, appointmentNo });
    if (body.blNo) {
      if (body.blNo !== 'MEDUJD260001') return [404, { detail: `Bill of lading ${body.blNo} was not found at this terminal.` }];
      return [200, { data: { category: body.category, blNo: body.blNo, containers: [
        row('MSCU1234566', '22G1', 'Ready'), row('MSCU5523417', '22G1', 'Ready'), row('MEDU2601003', '45G1', 'Ready'),
        row('TGHU1029384', '22G1', 'Ready'), row('MEDU2601014', '22G1', 'Ready'),
        row('MSCU7654329', '22G1', 'Blocked', ['Customs hold on MSCU7654329']),
        row('CMAU5100028', '42G1', 'Warning', [], ['Storage charges on CMAU5100028 start today']),
        row('MSCU4410022', '22G1', 'Booked', ['Already booked on DEMO-261015-2PL7S'], [], 'DEMO-261015-2PL7S'),
      ] } }];
    }
    return [200, { data: { category: body.category, blNo: null, containers: body.containers.map(no =>
      no === 'MSCU1234567' ? row(no, null, 'Blocked', [`${no} has a wrong check digit — please re-check the number`]) : row(no, null, 'Ready')) } }];
  }
  if (path.endsWith('/appointments') && method === 'POST') {
    if (body.items.some(i => i.containerNo === 'MSCU7654329'))
      return [422, { title: 'Not cleared for booking', detail: 'Item 1: customs hold.', errors: ['Item 1: customs hold.'], checks: [check('CustomsRelease', 'Fail', 'MSCU7654329: customs hold (inspection)')] }];
    const slot = availability('2026-10-16').find(x => x.slotId === body.slotId) ?? availability('2026-10-16')[4];
    if (body.items.some(i => i.containerNo === 'MEDU2601014')) return [409, { title: 'No capacity', detail: 'This slot is full for Import pickup.' }];
    globalThis.__n = (globalThis.__n ?? 0) + 1;
    return [201, { ...appointments[2], appointmentNo: `DEMO-261016-${['9TF3A', '4KD8E', '7QW2R', '2MN6P', '5HX1C'][globalThis.__n % 5]}`, status: 'Confirmed', slotStart: slot.startAt, slotEnd: slot.endAt,
      trafficLight: body.items.some(i => i.containerNo === 'CMAU5100028') ? 'Amber' : 'Green', items: body.items.map(i => item(i.category, i.containerNo, ok)) }];
  }
  if (path.endsWith('/report-subscriptions') && method === 'GET') return [200, [
    { subscriptionId: 1, email: 'ops.manager@demo-terminal.example', language: 'en', daily: true, weekly: true, lastDailyOn: today, lastWeeklyOn: '2026-10-11' },
    { subscriptionId: 2, email: 'gm@demo-terminal.example', language: 'ar', daily: false, weekly: true, lastDailyOn: null, lastWeeklyOn: '2026-10-11' }]];
  if (path.endsWith('/safety/campaigns') && method === 'GET') return [200, [
    { campaignId: 2, imageUrl: null, startsOn: '2026-10-12', endsOn: '2026-11-12', companyIds: null, requireAck: true, archived: false, active: true, confirmed: 41,
      texts: { en: { title: 'Stay in your cab under the crane', body: 'When a straddle carrier or a crane is working on your truck, stay in the cab with the door closed. Step out only when the clerk tells you.' },
        ar: { title: 'ابقَ في المقصورة تحت الرافعة', body: 'عندما تعمل الرافعة على شاحنتك ابقَ داخل المقصورة والباب مغلق، ولا تنزل إلا إذا طلب منك الموظف ذلك.' } } },
    { campaignId: 1, imageUrl: null, startsOn: '2026-10-01', endsOn: '2026-10-31', companyIds: [7], requireAck: false, archived: false, active: true, confirmed: 12,
      texts: { en: { title: 'Speed limit 25 km/h on the quay road', body: 'Speed cameras are working on the quay road. Every reading over 25 km/h goes on the driver record.' } } },
    { campaignId: 0, imageUrl: null, startsOn: '2026-09-01', endsOn: '2026-09-30', companyIds: null, requireAck: true, archived: true, active: false, confirmed: 133,
      texts: { en: { title: 'Summer heat: drink water', body: 'Keep water in the cab and rest in the shaded truck parking while you wait.' } } }]];
  if (path.endsWith('/safety/induction') && method === 'GET') return [200, {
    version: 3, updatedAt: iso('2026-10-01', 9), validMonths: 12, validPasses: 214,
    pages: [
      { texts: { en: { title: 'Welcome to the terminal', body: 'Wear your vest and safety shoes at all times outside the cab. Follow the signs and the app to your block.' },
        ar: { title: 'أهلاً بك في المحطة', body: 'ارتدِ السترة وحذاء السلامة دائماً خارج المقصورة، واتبع الإشارات والتطبيق للوصول إلى الموقع.' } } },
      { texts: { en: { title: 'Speed and right of way', body: '25 km/h everywhere. Straddle carriers and reach stackers always go first.' } } }],
    questions: [
      { correct: 1, texts: { en: { question: 'What is the speed limit inside the terminal?', options: ['40 km/h', '25 km/h', '60 km/h'] } } },
      { correct: 0, texts: { en: { question: 'A straddle carrier is coming. What do you do?', options: ['Stop and let it pass', 'Drive past quickly', 'Sound the horn'] } } }] }];
  if (path.endsWith('/capacity/advice/decide')) {
    globalThis.__adviceDone = [...(globalThis.__adviceDone ?? []), body.key];
    return [200, { data: { key: body.key }, regeneratedSlots: null }];
  }
  if (path.endsWith('/capacity/advice')) {
    // "Next Tuesday" on the day the demo is shown (demo.js moves the dates by whole days).
    const shift = Math.round((globalThis.__demoShiftMs ?? 0) / 864e5);
    const add = (x, n) => { const [y, m, d] = x.split('-').map(Number); return new Date(Date.UTC(y, m - 1, d + n)).toISOString().slice(0, 10); };
    const shown = add(today, shift);
    const ahead = ((2 - new Date(shown).getUTCDay()) + 7) % 7 || 7;
    const busy = add(today, ahead);
    const all = [
      { key: `busy|${busy}`, kind: 'BusyDay', day: null, start: null, end: null, date: busy, current: 620, suggested: 769, sample: 8,
        noShowRate: null, avgWaitMinutes: null, utilization: 1.18, forecast: 732 },
      { key: 'overbook|Sunday|06:00', kind: 'Overbook', day: 'Sunday', start: '06:00', end: '13:00', date: null, current: 5, suggested: 7, sample: 1840,
        noShowRate: 0.092, avgWaitMinutes: null, utilization: null, forecast: null },
      { key: 'capacity|Monday|14:00', kind: 'LowerCapacity', day: 'Monday', start: '14:00', end: '22:00', date: null, current: 40, suggested: 34, sample: 1512,
        noShowRate: null, avgWaitMinutes: 27.4, utilization: 0.93, forecast: null },
      { key: 'capacity|Thursday|06:00', kind: 'RaiseCapacity', day: 'Thursday', start: '06:00', end: '13:00', date: null, current: 40, suggested: 44, sample: 1630,
        noShowRate: null, avgWaitMinutes: 6.1, utilization: 0.97, forecast: null },
    ];
    const done = globalThis.__adviceDone ?? [];
    return [200, { mode: 1, historyDays: 56, visits: 21480, enoughHistory: true, suggestions: all.filter(a => !done.includes(a.key)) }];
  }
  return [404, { detail: `No mock for ${method} ${path}` }];
}
