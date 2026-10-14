import type { UserRole } from '../api/types';
import { canManageUsers } from '../auth/Auth';
import type { MessageKey } from '../i18n/en';

export interface NavItem {
  path: string;
  label: MessageKey;
}

/** The sections a role sees, in order. Pages check access again, and so does the API. */
export function navFor(role: UserRole): NavItem[] {
  const items: NavItem[] = [{ path: '/appointments', label: 'navAppointments' }];
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
