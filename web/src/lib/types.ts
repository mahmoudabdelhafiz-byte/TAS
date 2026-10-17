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
  /** Signs in with a code from an authenticator app. */
  twoStepEnabled?: boolean;
  /** An admin who must set up two-step sign-in before anything else. */
  twoStepSetupRequired?: boolean;
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
  /** ISO code of the terminal's country pack (holidays, currency). */
  country?: string;
  currency?: string;
  /** The terminal has the Yardsyncs Autogate module (lane devices, automatic lanes, remote clerk, inspection). */
  autogate?: boolean;
}

/** The installation's country pack: how mobiles are typed and the drivers' ID document. */
export interface Country {
  code: string;
  nameEn: string;
  nameAr: string;
  dialCode: string;
  mobileExample: string;
  idNameEn: string;
  idNameAr: string;
  currency: string;
  currencyDecimals: number;
  timeZone: string;
}

/** A public holiday on one day; lunar ones are expected dates (they follow the moon sighting). */
export interface Holiday {
  date: string;
  nameEn: string;
  nameAr: string;
  lunar: boolean;
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
  /** Which way the cargo goes: In (brought into the terminal), Out (taken out); null = not checked at the exit. */
  flow?: 'In' | 'Out' | null;
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
  /** The seal number the company declared. */
  sealNo?: string | null;
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

export type TroubleReason = 'NoAppointment' | 'Early' | 'Late' | 'NotCleared' | 'Blocked' | 'AlreadyIn' | 'Cancelled' | 'DriverUnknown' | 'SealMismatch';

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

export type LaneState = 'Open' | 'Closed' | 'OutOfOrder';
export type LaneMode = 'Manual' | 'Automatic';
export type LaneDeviceKind = 'Camera' | 'Ocr' | 'RfidReader' | 'Kiosk' | 'Barrier' | 'TrafficLight' | 'Display' | 'Printer' | 'Intercom' | 'Weighbridge';
export type LaneRead = 'Plate' | 'Container' | 'IsoCode' | 'Seal' | 'Damage' | 'Tag';
export type LaneIdentification = 'AppointmentNo' | 'GatePass' | 'Plate' | 'Container' | 'RfidTag';
export type LaneProblem = 'NoBarrier' | 'NoIdentification';

export interface LaneDevice {
  deviceId: number;
  deviceCode: string;
  kind: LaneDeviceKind;
  reads: LaneRead[];
  vendor: string | null;
  model: string | null;
  address: string | null;
  isActive: boolean;
  notes: string | null;
  /** A device key has been issued. */
  hasKey: boolean;
  connection: DeviceConnection;
  lastSeenAt: string | null;
  statusDetail: string | null;
}

export type DeviceConnection = 'NotConnected' | 'Online' | 'Fault' | 'Offline';

export interface DeviceRead {
  readId: number;
  deviceCode: string;
  readAt: string;
  receivedAt: string;
  plate: string | null;
  plateConfidence: number | null;
  containers: { number: string; checkDigitOk: boolean; isoCode: string | null; confidence: number | null; position: string | null }[];
  seals: string[];
  damage: { photo: string | null; note: string | null; side: string | null }[];
  photos: string[];
  tag: string | null;
  appointmentNo: string | null;
  gatePassScanned: boolean;
  weightKg: number | null;
  warnings: string[];
}

export interface Lane {
  laneId: number;
  gateCode: string;
  laneCode: string;
  direction: 'In' | 'Out';
  /** False when the lane is retired; `state` is the day-to-day open / closed. */
  isActive: boolean;
  state: LaneState;
  mode: LaneMode;
  stateNote: string | null;
  stateChangedAt: string | null;
  devices: LaneDevice[];
  identifies: LaneIdentification[];
  /** What the lane still needs to work automatically; empty when it could. */
  automaticProblems: LaneProblem[];
  /** The lane's latest device read in the last few minutes. */
  lastRead: DeviceRead | null;
  /** The truck the lane controller is handling or handled last, in the last few minutes. */
  transaction: LaneTransaction | null;
}

export type LaneTransactionStatus = 'Open' | 'Admitted' | 'Referred' | 'Passed' | 'Abandoned';

export interface LaneTransaction {
  transactionId: number;
  laneId: number;
  laneCode: string;
  status: LaneTransactionStatus;
  startedAt: string;
  lastReadAt: string;
  decidedAt: string | null;
  closedAt: string | null;
  readCount: number;
  plate: string | null;
  containers: string[];
  appointmentNo: string | null;
  gatePassScanned: boolean;
  weightKg: number | null;
  troubleId: number | null;
  reason: string | null;
  message: string | null;
  /** A clerk corrected what the cameras read. */
  corrected: boolean;
}

export interface LaneCommandView {
  commandId: number;
  deviceCode: string;
  command: string;
  text: string | null;
  createdAt: string;
  expiresAt: string;
  deliveredAt: string | null;
  doneAt: string | null;
  result: 'Done' | 'Failed' | null;
  resultDetail: string | null;
  progress: 'Waiting' | 'Delivered' | 'Done' | 'Failed' | 'Expired';
}

export interface LaneTransactionDetail {
  transaction: LaneTransaction;
  reads: DeviceRead[];
  commands: LaneCommandView[];
  trouble: Trouble | null;
  devices: LaneDevice[];
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

export type InspectionStage = 'In' | 'Out';
export type SealStatus = 'Ok' | 'Mismatch' | 'Missing' | 'Broken' | 'NotChecked';
export type DamageSide = 'Front' | 'Rear' | 'Left' | 'Right' | 'Top' | 'Underside' | 'Inside' | 'Door';
export type DamageKind = 'Dent' | 'Hole' | 'Cut' | 'Scratch' | 'Rust' | 'Broken' | 'Missing' | 'Wet' | 'Other';
export type DamageSeverity = 'Minor' | 'Major';
export type DisputeStatus = 'Open' | 'Accepted' | 'Rejected';

export interface DamageEntry {
  damageId?: number;
  side: DamageSide | null;
  kind: DamageKind;
  severity: DamageSeverity;
  note: string | null;
  photoRef: string | null;
  photoId: number | null;
}

export interface Dispute {
  disputeId: number;
  status: DisputeStatus;
  reason: string;
  openedAt: string;
  resolutionNote: string | null;
  resolvedAt: string | null;
}

export interface Inspection {
  inspectionId: number;
  stage: InspectionStage;
  containerNo: string | null;
  sealExpected: string | null;
  sealSeen: string | null;
  sealStatus: SealStatus;
  damageFound: boolean;
  notes: string | null;
  source: 'Camera' | 'Clerk';
  inspectedAt: string;
  damage: DamageEntry[];
  photoIds: number[];
  dispute: Dispute | null;
}

export interface AppointmentInspections {
  appointmentNo: string;
  status: string;
  companyName: string;
  truckPlate: string | null;
  items: { index: number; containerNo: string | null; category: string; sealNo: string | null }[];
  inspections: Inspection[];
  newAtGateOut: { containerNo: string | null; side: DamageSide | null; kind: DamageKind; severity: DamageSeverity }[];
}

export interface InspectionRow {
  inspectionId: number;
  appointmentNo: string;
  companyName: string;
  containerNo: string | null;
  stage: InspectionStage;
  inspectedAt: string;
  source: 'Camera' | 'Clerk';
  sealStatus: SealStatus;
  damageCount: number;
  dispute: DisputeStatus | null;
  disputeId: number | null;
}
