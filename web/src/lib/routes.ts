// Hash routes (#/appointments?date=2026-10-20&no=DEMO-…) and which screens each role gets.

import type { UserRole } from './types.ts';

export type Screen = 'book' | 'track' | 'bulk' | 'appointments' | 'slots' | 'setup' | 'onboard' | 'charges' | 'map' | 'control' | 'gate' | 'lanes' | 'kpi' | 'fleet' | 'help';

export interface Route {
  screen: Screen | null;
  params: Record<string, string>;
}

export function parseHash(hash: string): Route {
  const clean = hash.replace(/^#\/?/, '');
  const [path, query = ''] = clean.split('?');
  const params: Record<string, string> = {};
  for (const [key, value] of new URLSearchParams(query)) params[key] = value;
  const screen = (['book', 'track', 'bulk', 'appointments', 'slots', 'setup', 'onboard', 'charges', 'map', 'control', 'gate', 'lanes', 'kpi', 'fleet', 'help'] as const).find(s => s === path) ?? null;
  return { screen, params };
}

export function buildHash(screen: Screen, params: Record<string, string | undefined> = {}): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value) query.set(key, value);
  const text = query.toString();
  return `#/${screen}${text ? `?${text}` : ''}`;
}

/** The menu for a role, in order; the first entry is the home screen. Help is last for everyone. */
export function screensFor(role: UserRole): Screen[] {
  return [...workScreens(role), 'help'];
}

function workScreens(role: UserRole): Screen[] {
  switch (role) {
    case 'CompanyAdmin':
      return ['book', 'track', 'bulk', 'appointments', 'fleet', 'charges'];
    case 'CompanyUser':
      return ['book', 'track', 'bulk', 'appointments', 'fleet'];
    case 'GateClerk':
      return ['gate', 'control', 'appointments'];
    case 'Planner':
      return ['slots', 'control', 'kpi', 'setup', 'map', 'appointments', 'track', 'book', 'bulk', 'gate', 'fleet', 'charges'];
    case 'TerminalAdmin':
    case 'OperatorAdmin':
    case 'SysAdmin':
      return ['slots', 'control', 'kpi', 'setup', 'onboard', 'map', 'appointments', 'track', 'book', 'bulk', 'gate', 'lanes', 'fleet', 'charges'];
  }
}

export const isCompanyRole = (role: UserRole) => role === 'CompanyAdmin' || role === 'CompanyUser';
export const canChangeCapacity = (role: UserRole) => role === 'Planner' || isAdminRole(role);
/** Terminal admins, operator admins (a terminal admin at each of the operator's terminals) and system admins. */
export const isAdminRole = (role: UserRole) => role === 'TerminalAdmin' || role === 'OperatorAdmin' || role === 'SysAdmin';
