// Turning a list of containers into truck visits and spreading them over the chosen hours.
// Capacity is counted in moves: a truck carrying two containers uses two places in its hour.

export interface PlanContainer {
  containerNo: string;
  isoType: string | null;
  sizeFt: number | null;
}

export interface PlanSlot {
  slotId: number;
  startAt: string;
  endAt: string;
  available: number;
}

export interface Visit {
  slot: PlanSlot;
  containers: PlanContainer[];
}

/**
 * One container per truck, or two 20′ containers per truck when `pairTwenties` is on.
 * 40′ / 45′ always go alone; unknown sizes are paired only when `pairUnknown` is on (typed numbers have no size).
 */
export function groupTrucks(containers: PlanContainer[], pairTwenties: boolean, pairUnknown = false): PlanContainer[][] {
  const trucks: PlanContainer[][] = [];
  let waiting: PlanContainer | null = null;
  for (const c of containers) {
    const pairable = pairTwenties && (c.sizeFt === 20 || (c.sizeFt === null && pairUnknown));
    if (!pairable) {
      trucks.push([c]);
      continue;
    }
    if (waiting) {
      trucks.push([waiting, c]);
      waiting = null;
    } else {
      waiting = c;
    }
  }
  if (waiting) trucks.push([waiting]);
  return trucks;
}

/** Fills the hours in time order; a truck goes into the first hour with room for all its containers. */
export function allocate(trucks: PlanContainer[][], slots: PlanSlot[]): { visits: Visit[]; unplaced: PlanContainer[][] } {
  const ordered = [...slots].sort((a, b) => a.startAt.localeCompare(b.startAt));
  const room = new Map(ordered.map(s => [s.slotId, Math.max(0, s.available)]));
  const visits: Visit[] = [];
  const unplaced: PlanContainer[][] = [];
  for (const truck of trucks) {
    const slot = ordered.find(s => (room.get(s.slotId) ?? 0) >= truck.length);
    if (!slot) {
      unplaced.push(truck);
      continue;
    }
    room.set(slot.slotId, room.get(slot.slotId)! - truck.length);
    visits.push({ slot, containers: truck });
  }
  visits.sort((a, b) => a.slot.startAt.localeCompare(b.slot.startAt)); // stable: trucks of one hour keep their order
  return { visits, unplaced };
}

/** Container numbers from pasted text: lines, commas, semicolons, tabs; spaces only between whole numbers. */
export function splitContainers(text: string): string[] {
  const out: string[] = [];
  for (const part of text.split(/[\n\r,;\t]+/)) {
    const compact = part.replace(/[^A-Za-z0-9]/g, '').toUpperCase();
    if (!compact) continue;
    if (compact.length > 11 && compact.length % 11 === 0 && part.trim().includes(' ')) {
      for (const piece of part.trim().split(/\s+/)) {
        const p = piece.replace(/[^A-Za-z0-9]/g, '').toUpperCase();
        if (p) out.push(p);
      }
    } else {
      out.push(compact);
    }
  }
  return [...new Set(out)];
}
