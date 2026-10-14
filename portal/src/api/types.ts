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
  bookingHorizonDays: number;
}

export interface Category {
  categoryId: number;
  code: string;
  nameEn: string;
  nameAr: string;
  mode: 'Container' | 'Cargo';
  unitType: 'Move' | 'Trip';
  sortOrder: number;
  isActive: boolean;
}

export interface SlotAvailability {
  slotId: number;
  startAt: string;
  endAt: string;
  isClosed: boolean;
  available: number;
}

export interface Truck {
  truckId: number;
  companyId: number;
  plateNumber: string;
  plateNormalized: string;
  isBlocked: boolean;
}

export interface Driver {
  driverId: number;
  companyId: number;
  fullName: string;
  idNumber: string;
  mobile: string;
  preferredLanguage: string;
  isBlocked: boolean;
}

export interface Cargo {
  cargoAllocationId: number;
  companyId: number | null;
  cargoRef: string;
  commodity: string | null;
  totalTrips: number | null;
  bookedTrips: number;
  status: string;
}

export type CheckType =
  | 'ContainerNumber' | 'Availability' | 'CustomsRelease' | 'LineRelease' | 'TerminalHold' | 'Payment'
  | 'BookingValid' | 'Vgm' | 'Cutoff' | 'CargoRelease' | 'TosUnavailable';

export interface Check {
  item: number | null;
  type: CheckType;
  result: 'Pass' | 'Warn' | 'Fail';
  message: string | null;
  source: string;
}

export interface BookingItem {
  category: string;
  containerNo?: string;
  bookingRef?: string;
  cargoRef?: string;
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
  checks: Check[];
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
  /** Booking refused because a check failed (422): every check with its result. */
  checks?: Check[];
}

export interface BulkVisitResult {
  visit: string;
  rows: number[];
  slot: string;
  status: 'Booked' | 'WouldBook' | 'Failed';
  appointmentNo: string | null;
  errors: string[];
  checks: Check[];
}

export interface BulkResult {
  dryRun: boolean;
  visits: number;
  succeeded: number;
  failed: number;
  fileErrors: { row: number; message: string }[];
  results: BulkVisitResult[];
}

/** Configuration changes come back as { data, regeneratedSlots }. */
export interface AdminResponse<T> {
  data: T;
}
