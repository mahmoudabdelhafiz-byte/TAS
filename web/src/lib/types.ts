// Shapes returned by the Yardsyncs API (enums arrive as their C# names, e.g. "EnRoute").

export type UserRole = 'CompanyAdmin' | 'CompanyUser' | 'GateClerk' | 'Planner' | 'TerminalAdmin' | 'OperatorAdmin' | 'SysAdmin';

export interface UserProfile {
  userId: number;
  email: string;
  fullName: string;
  role: UserRole;
  companyId: number | null;
  companyName: string | null;
  terminalId: number | null;
  preferredLanguage: string;
  /** For an operator admin: the operator whose terminals they run. */
  operatorId?: number | null;
  operatorName?: string | null;
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
  operatorId?: number | null;
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

export interface Company {
  companyId: number;
  name: string;
  status: 'Active' | 'Suspended' | string;
}

export interface Truck {
  truckId: number;
  companyId: number;
  plateNumber: string;
  isBlocked: boolean;
}

export interface Driver {
  driverId: number;
  companyId: number;
  fullName: string;
  mobile: string;
  preferredLanguage: string;
  isBlocked: boolean;
}

export interface SlotAvailability {
  slotId: number;
  startAt: string;
  endAt: string;
  isClosed: boolean;
  available: number;
}

export type AppointmentStatus =
  | 'Draft' | 'Confirmed' | 'EnRoute' | 'Arrived' | 'GateIn' | 'Completed' | 'Cancelled' | 'NoShow' | 'RejectedAtGate';

export type Light = 'Grey' | 'Green' | 'Amber' | 'Red';

export interface Check {
  item: number | null;
  type: string;
  result: 'Pass' | 'Warn' | 'Fail';
  message: string | null;
  source: string;
}

export interface AppointmentItem {
  category: string;
  containerNo: string | null;
  isoType: string | null;
  bookingRef: string | null;
  edoRef: string | null;
  cargoRef: string | null;
  plannedTonnage: number | null;
  validationStatus: string;
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
  trafficLight: Light;
  trafficReason: string;
  qrToken: string;
  items: AppointmentItem[];
  createdAt: string;
  eta?: string | null;
}

export interface BookingItemInput {
  category: string;
  containerNo?: string;
  bookingRef?: string;
  cargoRef?: string;
  plannedTonnage?: number;
}

export interface NotificationView {
  template: string;
  channel: string;
  language: string;
  status: string;
  recipient: string | null;
  attempts: number;
  lastError: string | null;
  createdAt: string;
  sentAt: string | null;
}

export interface SlotBoardRow {
  slotId: number;
  startAt: string;
  endAt: string;
  local: string;
  totalCapacity: number;
  totalBooked: number;
  isClosed: boolean;
  overCapacity: boolean;
  categories: { category: string; quota: number; booked: number }[];
}

export type OverrideAction = 'Set' | 'Adjust' | 'Close';

export interface Override {
  overrideId: number;
  startAt: string;
  endAt: string;
  category: string | null;
  action: OverrideAction;
  value: number | null;
  reason: string;
  isActive: boolean;
  createdAt: string;
}

export interface AdminResponse<T> {
  data: T;
  regeneratedSlots: unknown;
}

export interface GateBoardTruck {
  appointmentNo: string;
  companyName: string;
  truckPlate: string | null;
  status: AppointmentStatus;
  arrivedAt: string | null;
  gateInAt: string | null;
  minutesSinceArrival: number;
}

export interface GateBoard {
  atGate: GateBoardTruck[];
  inTerminal: GateBoardTruck[];
  openTroubles: number;
  completedToday: number;
  averageTurnMinutesToday: number | null;
}

export type TroubleReason = 'NoAppointment' | 'Early' | 'Late' | 'NotCleared' | 'Blocked' | 'AlreadyIn' | 'Cancelled';

export interface Trouble {
  troubleId: number;
  lane: string | null;
  appointmentNo: string | null;
  plateRead: string | null;
  containerReads: string | null;
  reason: TroubleReason;
  reasonText: string;
  status: 'Open' | 'Resolved';
  resolution: string | null;
  note: string | null;
  createdAt: string;
}

export interface Lane {
  laneId: number;
  gateCode: string;
  laneCode: string;
  direction: 'In' | 'Out';
  isActive: boolean;
}

export interface ArrivalResult {
  decision: 'Admit' | 'Refer';
  message: string;
  appointmentNo: string | null;
  matchedBy: string;
  light: Light | null;
  troubleId: number | null;
  reason: TroubleReason | null;
  warnings: string[];
}

export type LookupStatus = 'Ready' | 'Warning' | 'Blocked' | 'Booked';

export interface LookupContainer {
  containerNo: string;
  isoType: string | null;
  sizeFt: number | null;
  status: LookupStatus;
  problems: string[];
  warnings: string[];
  appointmentNo: string | null;
}

export interface ContainerLookupResult {
  category: string;
  blNo: string | null;
  containers: LookupContainer[];
}

/** A terminal area (GET /api/terminals/{id}/geofences); GeoJson is the Polygon. */
export interface GeofenceView {
  geofenceId: number;
  name: string;
  zoneType: string;
  yardBlock: string | null;
  triggersCheckIn: boolean;
  isActive: boolean;
  geoJson: string;
}

export interface ControlStop {
  sequence: number;
  stopType: string;
  block: string | null;
  slot: string | null;
  status: 'PENDING' | 'ACTIVE' | 'ARRIVED' | 'DONE' | 'CANCELLED';
  arrivedAt: string | null;
  completedAt: string | null;
}

export type ControlStage = 'ON_THE_WAY' | 'AT_GATE' | 'TO_BLOCK' | 'AT_BLOCK' | 'TO_EXIT';

export interface ControlTruck {
  appointmentNo: string;
  status: string;
  stage: ControlStage;
  company: string;
  truckPlate: string | null;
  driverName: string | null;
  slotStart: string;
  slotEnd: string;
  eta: string | null;
  arrivedAt: string | null;
  gateInAt: string | null;
  minutesInside: number | null;
  position: [number, number] | null;
  positionAt: string | null;
  moves: string[];
  stops: ControlStop[];
  currentStop: number | null;
  overdue: boolean;
  silent: boolean;
}

export interface YardMapArea {
  name: string;
  kind: string;
  block: string | null;
  ring: [number, number][];
}

export interface ControlRoomBoard {
  now: string;
  turnTargetMinutes: number;
  stats: {
    onTheWay: number;
    atGate: number;
    inside: number;
    completedToday: number;
    avgMinutesInside: number | null;
    avgMinutesToBlock: number | null;
    avgMinutesAtBlock: number | null;
  };
  trucks: ControlTruck[];
  map: { areas: YardMapArea[]; roads: [number, number][][]; bounds: number[] };
}
