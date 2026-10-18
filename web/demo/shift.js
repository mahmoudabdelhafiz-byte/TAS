// How many days the sample data moves to reach today (Riyadh): set before mock.mjs is evaluated, because some sample
// times are "minutes ago" and are computed when it loads.
export const sampleDay = '2026-10-15';
export const DAY = 864e5;
const riyadhToday = new Date(Date.now() + 3 * 3600e3).toISOString().slice(0, 10);
export const shiftDays = Math.round((Date.parse(riyadhToday) - Date.parse(sampleDay)) / DAY);
globalThis.__demoShiftMs = shiftDays * DAY;
