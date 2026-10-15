// The weekly capacity plan as a grid: one cell per day and slot start, edited in the portal and saved as windows.

export const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'] as const;
export type Day = (typeof days)[number];

/** A template window as the API sends and takes it. */
export interface PlanWindow {
  day: Day;
  start: string; // "HH:mm"
  end: string; // "HH:mm", "24:00" = midnight
  totalCapacity: number;
  overbookPct: number;
  quotas: Record<string, number>;
}

/** Capacity of one slot in the grid; no cell = closed (no slots generated). */
export interface Cell {
  total: number;
  overbookPct: number;
  quotas: Record<string, number>;
}

export type Grid = Map<string, Cell>;

export const cellKey = (day: Day, minute: number) => `${day}|${minute}`;

export function toMinutes(time: string): number {
  const [h, m] = time.split(':').map(Number);
  return h * 60 + (m || 0);
}

export function toTime(minutes: number): string {
  return `${String(Math.floor(minutes / 60)).padStart(2, '0')}:${String(minutes % 60).padStart(2, '0')}`;
}

/** Slot starts in a day for a slot length. */
export function slotStarts(stepMinutes: number): number[] {
  const out: number[] = [];
  for (let m = 0; m < 1440; m += stepMinutes) out.push(m);
  return out;
}

export function expand(windows: PlanWindow[], stepMinutes: number): Grid {
  const grid: Grid = new Map();
  for (const w of windows) {
    const start = toMinutes(w.start);
    const end = w.end === '23:59' ? 1440 : toMinutes(w.end);
    const first = Math.floor(start / stepMinutes) * stepMinutes;
    for (let m = first; m < end; m += stepMinutes) {
      grid.set(cellKey(w.day, m), { total: w.totalCapacity, overbookPct: Number(w.overbookPct) || 0, quotas: { ...(w.quotas ?? {}) } });
    }
  }
  return grid;
}

export function sameCell(a: Cell, b: Cell): boolean {
  if (a.total !== b.total || a.overbookPct !== b.overbookPct) return false;
  const ka = Object.keys(a.quotas).sort();
  const kb = Object.keys(b.quotas).sort();
  return ka.length === kb.length && ka.every((k, i) => k === kb[i] && a.quotas[k] === b.quotas[k]);
}

/** Back to windows: consecutive slots of a day with the same capacity become one window. */
export function compress(grid: Grid, stepMinutes: number): PlanWindow[] {
  const windows: PlanWindow[] = [];
  for (const day of days) {
    let open: { start: number; cell: Cell } | null = null;
    const close = (end: number) => {
      if (!open) return;
      windows.push({
        day, start: toTime(open.start), end: end >= 1440 ? '24:00' : toTime(end),
        totalCapacity: open.cell.total, overbookPct: open.cell.overbookPct, quotas: { ...open.cell.quotas },
      });
      open = null;
    };
    for (const m of slotStarts(stepMinutes)) {
      const cell = grid.get(cellKey(day, m));
      if (open && (!cell || !sameCell(open.cell, cell))) close(m);
      if (cell && !open) open = { start: m, cell };
    }
    close(1440);
  }
  return windows;
}

/** Cells in the rectangle between two corners (day index × slot start). */
export function rectangle(a: { day: number; minute: number }, b: { day: number; minute: number }, stepMinutes: number): string[] {
  const keys: string[] = [];
  const [d0, d1] = [Math.min(a.day, b.day), Math.max(a.day, b.day)];
  const [m0, m1] = [Math.min(a.minute, b.minute), Math.max(a.minute, b.minute)];
  for (let d = d0; d <= d1; d++) for (let m = m0; m <= m1; m += stepMinutes) keys.push(cellKey(days[d], m));
  return keys;
}

/** Slots per week and trucks per week the plan allows (for the summary line). */
export function weekTotals(grid: Grid): { slots: number; trucks: number } {
  let trucks = 0;
  for (const c of grid.values()) trucks += c.total;
  return { slots: grid.size, trucks };
}
