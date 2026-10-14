// Shapes of the TAS API (enums arrive as strings).

export type UserRole = 'CompanyAdmin' | 'CompanyUser' | 'GateClerk' | 'Planner' | 'TerminalAdmin' | 'SysAdmin';
export const companyRoles: readonly UserRole[] = ['CompanyAdmin', 'CompanyUser'];
export const terminalRoles: readonly UserRole[] = ['GateClerk', 'Planner', 'TerminalAdmin'];

export interface UserProfile {
  userId: number;
  email: string;
  fullName: string;
  role: UserRole;
  companyId: number | null;
  companyName: string | null;
  terminalId: number | null;
  preferredLanguage: string;
}

export interface TokenResponse {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
  refreshTokenExpiresAt: string;
  user: UserProfile;
}

export interface Terminal {
  terminalId: number;
  code: string;
  name: string;
  terminalType: string;
  timeZone: string;
  slotMinutes: number;
}

export type TrafficLight = 'Grey' | 'Green' | 'Amber' | 'Red';

export type AppointmentStatus =
  | 'Draft' | 'Confirmed' | 'EnRoute' | 'Arrived' | 'GateIn' | 'Completed' | 'Cancelled' | 'NoShow' | 'RejectedAtGate';

export interface AppointmentItem {
  category: string;
  containerNo: string | null;
  isoType: string | null;
  bookingRef: string | null;
  edoRef: string | null;
  cargoRef: string | null;
  plannedTonnage: number | null;
  validationStatus: 'Pending' | 'Pass' | 'Warn' | 'Fail';
}

export interface Appointment {
  appointmentNo: string;
  status: AppointmentStatus;
  slotId: number;
  slotStart: string;
  slotEnd: string;
  slotLocal: string;
  companyId: number;
  companyName: string;
  truckId: number | null;
  truckPlate: string | null;
  driverId: number | null;
  driverName: string | null;
  isDual: boolean;
  trafficLight: TrafficLight;
  trafficReason: string;
  items: AppointmentItem[];
  createdAt: string;
}

export interface UserView {
  userId: number;
  email: string;
  fullName: string;
  role: UserRole;
  companyId: number | null;
  terminalId: number | null;
  mobile: string | null;
  preferredLanguage: string;
  isActive: boolean;
  lastLoginAt: string | null;
  lockedUntil: string | null;
  passwordSet: boolean;
}

export interface Company {
  companyId: number;
  name: string;
  status: 'Pending' | 'Active' | 'Suspended';
}

export interface PasswordLinkInfo {
  purpose: 'Reset' | 'Invite';
  email: string;
  fullName: string;
  expiresAt: string;
}

/** RFC 7807 problem details as the API returns them. */
export interface Problem {
  title?: string;
  detail?: string;
  status?: number;
  errors?: string[] | Record<string, string[]>;
}
