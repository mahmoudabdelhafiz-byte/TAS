import type { UserRole } from '../api/types';
import { canManageUsers } from '../auth/Auth';
import type { MessageKey } from '../i18n/en';

export interface NavItem {
  path: string;
  label: MessageKey;
}

/** Companies book for themselves; planners and admins on behalf of companies. Gate clerks book walk-ins at the gate. */
export const canBook = (role: UserRole) => role !== 'GateClerk';

/** The sections a role sees, in order. Pages check access again, and so does the API. */
export function navFor(role: UserRole): NavItem[] {
  const items: NavItem[] = [{ path: '/appointments', label: 'navAppointments' }];
  if (canBook(role)) items.push({ path: '/book', label: 'navBook' });
  items.push({ path: '/fleet', label: 'navFleet' });
  if (canManageUsers(role)) items.push({ path: '/users', label: 'navUsers' });
  items.push({ path: '/account', label: 'navAccount' });
  return items;
}

const roleLabels: Record<UserRole, MessageKey> = {
  CompanyAdmin: 'roleCompanyAdmin',
  CompanyUser: 'roleCompanyUser',
  GateClerk: 'roleGateClerk',
  Planner: 'rolePlanner',
  TerminalAdmin: 'roleTerminalAdmin',
  SysAdmin: 'roleSysAdmin',
};

export const roleLabel = (role: UserRole): MessageKey => roleLabels[role];
