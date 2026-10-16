// The user guides (Markdown in en/ and ar/, one file per guide and language) and who sees which.

import type { Screen } from '../lib/routes.ts';

export interface Guide {
  id: string;
  /** Shown when the user's menu has any of these screens; empty = everyone. */
  screens: Screen[];
}

export const guides: Guide[] = [
  { id: 'start', screens: [] },
  { id: 'booking', screens: ['book', 'appointments'] },
  { id: 'track', screens: ['track'] },
  { id: 'fleet', screens: ['fleet'] },
  { id: 'driver', screens: ['book', 'fleet', 'gate'] },
  { id: 'gate', screens: ['gate'] },
  { id: 'control', screens: ['control'] },
  { id: 'planning', screens: ['setup', 'slots'] },
  { id: 'admin', screens: ['map'] },
];

export function guidesFor(screens: Screen[]): Guide[] {
  return guides.filter(g => g.screens.length === 0 || g.screens.some(s => screens.includes(s)));
}

/** The technical pages of the public documentation, in order (English only). */
export const techPages = ['overview', 'api', 'integration', 'tos-adapter', 'device-adapters', 'operations', 'security', 'assistant'] as const;
