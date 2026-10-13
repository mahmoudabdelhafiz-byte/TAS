/* =====================================================================
   Truck Appointment System (TAS) — SQL Server schema, draft v0.1
   Multi-terminal. See docs/data-model.md for the design.
   ===================================================================== */

/* ---------- 1. Terminal & configuration ---------- */

CREATE TABLE Terminal (
    TerminalId          INT IDENTITY PRIMARY KEY,
    Code                VARCHAR(20)   NOT NULL UNIQUE,
    Name                NVARCHAR(150) NOT NULL,
    TerminalType        VARCHAR(20)   NOT NULL CHECK (TerminalType IN ('CONTAINER','MULTIPURPOSE')),
    TimeZone            VARCHAR(50)   NOT NULL DEFAULT 'Arab Standard Time',
    SlotMinutes         INT           NOT NULL DEFAULT 60 CHECK (SlotMinutes IN (15,30,60,120)),
    BookingHorizonDays  INT           NOT NULL DEFAULT 14,
    IsActive            BIT           NOT NULL DEFAULT 1,
    CreatedAt           DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE TerminalSetting (
    TerminalId   INT          NOT NULL REFERENCES Terminal(TerminalId),
    SettingKey   VARCHAR(60)  NOT NULL,   -- EarlyGraceMin, LateGraceMin, CancelCutoffMin, MaxApptPerTruckPerDay ...
    SettingValue NVARCHAR(400) NOT NULL,
    PRIMARY KEY (TerminalId, SettingKey)
);

CREATE TABLE GateLane (
    LaneId      INT IDENTITY PRIMARY KEY,
    TerminalId  INT          NOT NULL REFERENCES Terminal(TerminalId),
    GateCode    VARCHAR(20)  NOT NULL,
    LaneCode    VARCHAR(20)  NOT NULL,
    Direction   VARCHAR(3)   NOT NULL CHECK (Direction IN ('IN','OUT')),
    IsActive    BIT          NOT NULL DEFAULT 1,
    UNIQUE (TerminalId, GateCode, LaneCode)
);

CREATE TABLE TransactionCategory (
    CategoryId  INT IDENTITY PRIMARY KEY,
    TerminalId  INT           NOT NULL REFERENCES Terminal(TerminalId),
    Code        VARCHAR(30)   NOT NULL,   -- IMP_PICKUP, EXP_DROP, MTY_RETURN, MTY_PICKUP, BB_DELIVERY, BULK_DELIVERY, RORO_PICKUP ...
    NameEn      NVARCHAR(100) NOT NULL,
    NameAr      NVARCHAR(100) NOT NULL,
    Mode        VARCHAR(10)   NOT NULL CHECK (Mode IN ('CONTAINER','CARGO')),
    UnitType    VARCHAR(10)   NOT NULL DEFAULT 'MOVE' CHECK (UnitType IN ('MOVE','TRIP')),
    SortOrder   INT           NOT NULL DEFAULT 0,
    IsActive    BIT           NOT NULL DEFAULT 1,
    UNIQUE (TerminalId, Code)
);

/* ---------- 2. Capacity configuration ---------- */

CREATE TABLE CapacityTemplate (
    TemplateId  INT IDENTITY PRIMARY KEY,
    TerminalId  INT           NOT NULL REFERENCES Terminal(TerminalId),
    Name        NVARCHAR(100) NOT NULL,       -- 'Default', 'Ramadan 2027', 'Summer'
    IsDefault   BIT           NOT NULL DEFAULT 0,
    ValidFrom   DATE          NULL,           -- NULL for default
    ValidTo     DATE          NULL,
    Priority    INT           NOT NULL DEFAULT 0,  -- higher wins among overlapping seasonal templates
    IsActive    BIT           NOT NULL DEFAULT 1,
    CONSTRAINT CK_Template_Dates CHECK (
        (IsDefault = 1 AND ValidFrom IS NULL AND ValidTo IS NULL) OR
        (IsDefault = 0 AND ValidFrom IS NOT NULL AND ValidTo IS NOT NULL AND ValidTo >= ValidFrom))
);
-- only one active default template per terminal
CREATE UNIQUE INDEX UX_Template_Default ON CapacityTemplate(TerminalId) WHERE IsDefault = 1 AND IsActive = 1;

CREATE TABLE CapacityTemplateSlot (
    TemplateSlotId  INT IDENTITY PRIMARY KEY,
    TemplateId      INT          NOT NULL REFERENCES CapacityTemplate(TemplateId),
    DayOfWeek       TINYINT      NOT NULL CHECK (DayOfWeek BETWEEN 0 AND 6),  -- 0 = Sunday
    StartTime       TIME(0)      NOT NULL,
    EndTime         TIME(0)      NOT NULL,   -- window; generator splits it by Terminal.SlotMinutes
    TotalCapacity   INT          NOT NULL CHECK (TotalCapacity >= 0),  -- per generated slot
    OverbookPct     DECIMAL(5,2) NOT NULL DEFAULT 0,
    CONSTRAINT CK_TemplateSlot_Time CHECK (EndTime > StartTime)
);

CREATE TABLE CapacityTemplateQuota (
    TemplateSlotId  INT NOT NULL REFERENCES CapacityTemplateSlot(TemplateSlotId),
    CategoryId      INT NOT NULL REFERENCES TransactionCategory(CategoryId),
    MaxUnits        INT NOT NULL CHECK (MaxUnits >= 0),
    PRIMARY KEY (TemplateSlotId, CategoryId)
);

CREATE TABLE CapacityOverride (
    OverrideId  INT IDENTITY PRIMARY KEY,
    TerminalId  INT           NOT NULL REFERENCES Terminal(TerminalId),
    StartAt     DATETIME2(0)  NOT NULL,   -- terminal local time
    EndAt       DATETIME2(0)  NOT NULL,
    CategoryId  INT           NULL REFERENCES TransactionCategory(CategoryId),  -- NULL = slot total
    Action      VARCHAR(10)   NOT NULL CHECK (Action IN ('SET','ADJUST','CLOSE')),
    Value       INT           NULL,       -- ignored for CLOSE; may be negative for ADJUST
    Reason      NVARCHAR(300) NOT NULL,
    IsActive    BIT           NOT NULL DEFAULT 1,
    CreatedBy   INT           NOT NULL,
    CreatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Override_Time CHECK (EndAt > StartAt),
    CONSTRAINT CK_Override_Value CHECK (Action = 'CLOSE' OR Value IS NOT NULL)
);
CREATE INDEX IX_Override_Range ON CapacityOverride(TerminalId, StartAt, EndAt) WHERE IsActive = 1;

CREATE TABLE QuotaReleaseRule (
    TerminalId    INT NOT NULL REFERENCES Terminal(TerminalId),
    CategoryId    INT NOT NULL REFERENCES TransactionCategory(CategoryId),
    HoursBefore   INT NOT NULL CHECK (HoursBefore >= 0),  -- unused quota opens to pool this many hours before slot start
    PRIMARY KEY (TerminalId, CategoryId)
);

/* ---------- 3. Parties ---------- */

CREATE TABLE Company (
    CompanyId     INT IDENTITY PRIMARY KEY,
    Name          NVARCHAR(200) NOT NULL,
    CrNumber      VARCHAR(30)   NULL,
    CompanyType   VARCHAR(20)   NOT NULL CHECK (CompanyType IN ('TRUCKING','FORWARDER','CONSIGNEE','LINE','OTHER')),
    IsPriority    BIT           NOT NULL DEFAULT 0,
    Status        VARCHAR(15)   NOT NULL DEFAULT 'ACTIVE' CHECK (Status IN ('PENDING','ACTIVE','SUSPENDED')),
    NoShowCount   INT           NOT NULL DEFAULT 0,
    CreatedAt     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE AppUser (
    UserId            INT IDENTITY PRIMARY KEY,
    CompanyId         INT           NULL REFERENCES Company(CompanyId),   -- NULL for terminal staff
    TerminalId        INT           NULL REFERENCES Terminal(TerminalId), -- set for terminal staff
    Email             VARCHAR(200)  NOT NULL UNIQUE,
    Mobile            VARCHAR(20)   NULL,
    FullName          NVARCHAR(150) NOT NULL,
    Role              VARCHAR(20)   NOT NULL CHECK (Role IN ('COMPANY_ADMIN','COMPANY_USER','GATE_CLERK','PLANNER','TERMINAL_ADMIN','SYS_ADMIN')),
    PreferredLanguage CHAR(2)       NOT NULL DEFAULT 'en',
    IsActive          BIT           NOT NULL DEFAULT 1
);

CREATE TABLE Truck (
    TruckId         INT IDENTITY PRIMARY KEY,
    CompanyId       INT          NOT NULL REFERENCES Company(CompanyId),
    PlateNumber     NVARCHAR(20) NOT NULL,   -- as entered (Arabic/Latin)
    PlateNormalized VARCHAR(20)  NOT NULL,   -- uppercase Latin, no spaces; used for OCR matching
    IsBlocked       BIT          NOT NULL DEFAULT 0,
    UNIQUE (PlateNormalized)
);

CREATE TABLE Driver (
    DriverId          INT IDENTITY PRIMARY KEY,
    CompanyId         INT           NOT NULL REFERENCES Company(CompanyId),
    FullName          NVARCHAR(150) NOT NULL,
    IdNumber          VARCHAR(30)   NOT NULL,
    Mobile            VARCHAR(20)   NOT NULL,
    PreferredLanguage CHAR(2)       NOT NULL DEFAULT 'ar',  -- ar / en / ur / hi
    IsBlocked         BIT           NOT NULL DEFAULT 0,
    UNIQUE (CompanyId, IdNumber)
);

/* ---------- 4. Materialized capacity ---------- */

CREATE TABLE Slot (
    SlotId         BIGINT IDENTITY PRIMARY KEY,
    TerminalId     INT          NOT NULL REFERENCES Terminal(TerminalId),
    StartAt        DATETIME2(0) NOT NULL,   -- terminal local time
    EndAt          DATETIME2(0) NOT NULL,
    TotalCapacity  INT          NOT NULL,   -- effective, incl. overrides + overbook
    TotalBooked    INT          NOT NULL DEFAULT 0,
    IsClosed       BIT          NOT NULL DEFAULT 0,
    OverCapacity   BIT          NOT NULL DEFAULT 0,   -- set when regenerated capacity < booked
    GeneratedAt    DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME(),
    RowVer         ROWVERSION,
    UNIQUE (TerminalId, StartAt)
);

CREATE TABLE SlotCategoryCapacity (
    SlotId         BIGINT NOT NULL REFERENCES Slot(SlotId),
    CategoryId     INT    NOT NULL REFERENCES TransactionCategory(CategoryId),
    QuotaCapacity  INT    NOT NULL,
    Booked         INT    NOT NULL DEFAULT 0,
    PRIMARY KEY (SlotId, CategoryId)
);

CREATE TABLE CapacityReservation (
    ReservationId  INT IDENTITY PRIMARY KEY,
    TerminalId     INT          NOT NULL REFERENCES Terminal(TerminalId),
    CompanyId      INT          NULL REFERENCES Company(CompanyId),  -- NULL = walk-in / emergency reserve
    CategoryId     INT          NULL REFERENCES TransactionCategory(CategoryId),
    StartAt        DATETIME2(0) NOT NULL,
    EndAt          DATETIME2(0) NOT NULL,
    UnitsPerSlot   INT          NOT NULL CHECK (UnitsPerSlot > 0),
    ReleaseHoursBefore INT      NOT NULL DEFAULT 0,  -- unused reserve opens to everyone this many hours before slot
    IsActive       BIT          NOT NULL DEFAULT 1
);

/* ---------- 5. Multipurpose cargo ---------- */

CREATE TABLE CargoAllocation (
    CargoAllocationId INT IDENTITY PRIMARY KEY,
    TerminalId        INT           NOT NULL REFERENCES Terminal(TerminalId),
    CompanyId         INT           NULL REFERENCES Company(CompanyId),
    CargoRef          VARCHAR(50)   NOT NULL,   -- BL / delivery order / cargo ref
    VesselVisitRef    VARCHAR(50)   NULL,
    Commodity         NVARCHAR(150) NULL,
    CargoType         VARCHAR(15)   NOT NULL CHECK (CargoType IN ('BREAKBULK','BULK','RORO','PROJECT')),
    TotalTonnage      DECIMAL(12,3) NULL,
    TotalTrips        INT           NULL,
    DeliveredTonnage  DECIMAL(12,3) NOT NULL DEFAULT 0,
    BookedTrips       INT           NOT NULL DEFAULT 0,
    Status            VARCHAR(15)   NOT NULL DEFAULT 'OPEN' CHECK (Status IN ('OPEN','HOLD','CLOSED')),
    UNIQUE (TerminalId, CargoRef)
);

/* ---------- 6. Booking ---------- */

CREATE TABLE Appointment (
    AppointmentId  BIGINT IDENTITY PRIMARY KEY,
    AppointmentNo  VARCHAR(20)  NOT NULL UNIQUE,
    TerminalId     INT          NOT NULL REFERENCES Terminal(TerminalId),
    SlotId         BIGINT       NOT NULL REFERENCES Slot(SlotId),
    CompanyId      INT          NOT NULL REFERENCES Company(CompanyId),
    TruckId        INT          NULL REFERENCES Truck(TruckId),    -- can be assigned after booking
    DriverId       INT          NULL REFERENCES Driver(DriverId),
    Status         VARCHAR(20)  NOT NULL DEFAULT 'CONFIRMED' CHECK (Status IN
                     ('DRAFT','CONFIRMED','EN_ROUTE','ARRIVED','GATE_IN','COMPLETED','CANCELLED','NO_SHOW','REJECTED_AT_GATE')),
    IsDual         BIT          NOT NULL DEFAULT 0,
    QrToken        VARCHAR(64)  NOT NULL UNIQUE,
    -- driver app / tracking (latest values; history in AppointmentEta & GeofenceEvent)
    TrafficLight   VARCHAR(6)   NOT NULL DEFAULT 'GREY' CHECK (TrafficLight IN ('GREY','GREEN','AMBER','RED')),
    TrafficReason  NVARCHAR(200) NULL,       -- shown to driver, e.g. 'Customs hold' / 'Outside time window'
    CurrentEta     DATETIME2(0) NULL,
    EtaUpdatedAt   DATETIME2(0) NULL,
    CheckInMethod  VARCHAR(15)  NULL CHECK (CheckInMethod IN ('APP_GEOFENCE','APP_MANUAL','OCR','CLERK')),
    ArrivedAt      DATETIME2(0) NULL,
    GateInAt       DATETIME2(0) NULL,
    GateOutAt      DATETIME2(0) NULL,
    CreatedBy      INT          NOT NULL REFERENCES AppUser(UserId),
    CreatedAt      DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME(),
    RowVer         ROWVERSION
);
CREATE INDEX IX_Appt_Slot   ON Appointment(SlotId, Status);
CREATE INDEX IX_Appt_Truck  ON Appointment(TruckId, Status);

CREATE TABLE AppointmentItem (
    ItemId             BIGINT IDENTITY PRIMARY KEY,
    AppointmentId      BIGINT        NOT NULL REFERENCES Appointment(AppointmentId),
    CategoryId         INT           NOT NULL REFERENCES TransactionCategory(CategoryId),
    -- container moves
    ContainerNo        VARCHAR(11)   NULL,
    IsoType            VARCHAR(4)    NULL,
    BookingRef         VARCHAR(50)   NULL,   -- export booking
    EdoRef             VARCHAR(50)   NULL,   -- import delivery order
    -- cargo moves
    CargoAllocationId  INT           NULL REFERENCES CargoAllocation(CargoAllocationId),
    Trips              INT           NULL,
    PlannedTonnage     DECIMAL(12,3) NULL,
    ActualTonnage      DECIMAL(12,3) NULL,
    -- validation
    ValidationStatus   VARCHAR(10)   NOT NULL DEFAULT 'PENDING' CHECK (ValidationStatus IN ('PENDING','PASS','WARN','FAIL')),
    TosRef             VARCHAR(50)   NULL,   -- e.g. N4 pre-advice / truck visit key
    CONSTRAINT CK_Item_Kind CHECK (
        (ContainerNo IS NOT NULL AND CargoAllocationId IS NULL) OR
        (ContainerNo IS NULL AND CargoAllocationId IS NOT NULL) OR
        (ContainerNo IS NULL AND CargoAllocationId IS NULL AND BookingRef IS NOT NULL)  -- empty pickup by booking only
    )
);
CREATE INDEX IX_Item_Container ON AppointmentItem(ContainerNo) WHERE ContainerNo IS NOT NULL;

CREATE TABLE ValidationResult (
    ValidationId  BIGINT IDENTITY PRIMARY KEY,
    ItemId        BIGINT        NOT NULL REFERENCES AppointmentItem(ItemId),
    CheckType     VARCHAR(20)   NOT NULL,   -- AVAILABILITY, CUSTOMS_RELEASE, LINE_RELEASE, TERMINAL_HOLD, PAYMENT, BOOKING_VALID, VGM, CUTOFF
    Result        VARCHAR(4)    NOT NULL CHECK (Result IN ('PASS','WARN','FAIL')),
    Message       NVARCHAR(400) NULL,
    Source        VARCHAR(20)   NOT NULL,   -- N4, FASAH, INTERNAL
    CheckedAt     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE AppointmentStatusHistory (
    HistoryId      BIGINT IDENTITY PRIMARY KEY,
    AppointmentId  BIGINT        NOT NULL REFERENCES Appointment(AppointmentId),
    FromStatus     VARCHAR(20)   NULL,
    ToStatus       VARCHAR(20)   NOT NULL,
    ChangedBy      INT           NULL REFERENCES AppUser(UserId),  -- NULL = system
    Reason         NVARCHAR(300) NULL,
    ChangedAt      DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);

/* ---------- 7. Gate & audit ---------- */

CREATE TABLE GateEvent (
    GateEventId    BIGINT IDENTITY PRIMARY KEY,
    TerminalId     INT          NOT NULL REFERENCES Terminal(TerminalId),
    LaneId         INT          NULL REFERENCES GateLane(LaneId),
    AppointmentId  BIGINT       NULL REFERENCES Appointment(AppointmentId),  -- NULL until matched
    EventType      VARCHAR(15)  NOT NULL CHECK (EventType IN
                     ('OCR_READ','APP_CHECKIN','PASS_SCAN','MATCHED','NO_APPT','EARLY','LATE','GATE_IN','GATE_OUT','TROUBLE')),
    PlateRead      VARCHAR(20)  NULL,
    ContainerRead  VARCHAR(11)  NULL,
    Details        NVARCHAR(400) NULL,
    OccurredAt     DATETIME2(0) NOT NULL
);
CREATE INDEX IX_Gate_Time ON GateEvent(TerminalId, OccurredAt);

/* ---------- 8. Driver app: identity, devices, gate pass ---------- */

CREATE TABLE DriverDevice (
    DeviceId      BIGINT IDENTITY PRIMARY KEY,
    DriverId      INT          NOT NULL REFERENCES Driver(DriverId),
    InstallId     VARCHAR(64)  NOT NULL,       -- app-generated install id
    Platform      VARCHAR(10)  NOT NULL CHECK (Platform IN ('ANDROID','IOS')),
    PushToken     VARCHAR(400) NULL,
    AppVersion    VARCHAR(20)  NULL,
    IsActive      BIT          NOT NULL DEFAULT 1,  -- one active device per driver (new login deactivates old)
    RegisteredAt  DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME(),
    LastSeenAt    DATETIME2    NULL,
    UNIQUE (InstallId)
);
CREATE UNIQUE INDEX UX_DriverDevice_Active ON DriverDevice(DriverId) WHERE IsActive = 1;

CREATE TABLE DriverVerification (
    VerificationId  BIGINT IDENTITY PRIMARY KEY,
    DriverId        INT           NOT NULL REFERENCES Driver(DriverId),
    Method          VARCHAR(20)   NOT NULL CHECK (Method IN ('MOBILE_OTP','ID_OTP','NAFATH','LIVENESS','CLERK_MANUAL')),
    Provider        VARCHAR(30)   NULL,      -- e.g. Nafath, liveness vendor
    ProviderRef     VARCHAR(100)  NULL,      -- vendor transaction id; no ID images stored here
    Status          VARCHAR(10)   NOT NULL CHECK (Status IN ('PENDING','VERIFIED','FAILED','EXPIRED','REVOKED')),
    VerifiedAt      DATETIME2     NULL,
    ExpiresAt       DATETIME2     NULL,
    VerifiedBy      INT           NULL REFERENCES AppUser(UserId),  -- for CLERK_MANUAL
    CreatedAt       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_DriverVerification ON DriverVerification(DriverId, Status);

CREATE TABLE GatePass (
    GatePassId   BIGINT IDENTITY PRIMARY KEY,
    DriverId     INT            NOT NULL REFERENCES Driver(DriverId),
    TerminalId   INT            NULL REFERENCES Terminal(TerminalId),  -- NULL = valid at all terminals
    PassSecret   VARBINARY(64)  NOT NULL,   -- encrypted seed for rotating (TOTP-style) QR in the app
    Status       VARCHAR(10)    NOT NULL DEFAULT 'ACTIVE' CHECK (Status IN ('ACTIVE','SUSPENDED','REVOKED','EXPIRED')),
    IssuedAt     DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    ExpiresAt    DATETIME2      NULL,
    RevokedReason NVARCHAR(200) NULL
);
CREATE INDEX IX_GatePass_Driver ON GatePass(DriverId, Status);

/* ---------- 9. Geofencing & ETA ---------- */

CREATE TABLE Geofence (
    GeofenceId   INT IDENTITY PRIMARY KEY,
    TerminalId   INT           NOT NULL REFERENCES Terminal(TerminalId),
    Name         NVARCHAR(100) NOT NULL,
    ZoneType     VARCHAR(15)   NOT NULL CHECK (ZoneType IN
                   ('APPROACH','PARKING','PRE_GATE','GATE','YARD_ZONE','CFS','EXIT')),
    Area         GEOGRAPHY     NOT NULL,   -- polygon (or buffered point)
    YardBlock    VARCHAR(20)   NULL,       -- link to TOS block for YARD_ZONE
    TriggersCheckIn BIT        NOT NULL DEFAULT 0,  -- entering this zone auto-checks-in the appointment
    IsActive     BIT           NOT NULL DEFAULT 1
);

CREATE TABLE GeofenceEvent (
    GeofenceEventId BIGINT IDENTITY PRIMARY KEY,
    AppointmentId   BIGINT       NOT NULL REFERENCES Appointment(AppointmentId),
    DriverId        INT          NOT NULL REFERENCES Driver(DriverId),
    GeofenceId      INT          NOT NULL REFERENCES Geofence(GeofenceId),
    EventType       VARCHAR(5)   NOT NULL CHECK (EventType IN ('ENTER','EXIT','DWELL')),
    Latitude        DECIMAL(9,6) NULL,
    Longitude       DECIMAL(9,6) NULL,
    OccurredAt      DATETIME2(0) NOT NULL
);
CREATE INDEX IX_GeofenceEvent_Appt ON GeofenceEvent(AppointmentId, OccurredAt);

CREATE TABLE AppointmentEta (   -- ETA snapshots, only while appointment is EN_ROUTE (privacy)
    EtaId          BIGINT IDENTITY PRIMARY KEY,
    AppointmentId  BIGINT        NOT NULL REFERENCES Appointment(AppointmentId),
    EtaAt          DATETIME2(0)  NOT NULL,
    DistanceKm     DECIMAL(8,2)  NULL,
    Latitude       DECIMAL(9,6)  NULL,
    Longitude      DECIMAL(9,6)  NULL,
    Source         VARCHAR(10)   NOT NULL CHECK (Source IN ('APP_GPS','TMS_API','MANUAL')),
    RecordedAt     DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Eta_Appt ON AppointmentEta(AppointmentId, RecordedAt);

/* ---------- 10. Yard guidance ---------- */

CREATE TABLE YardInstruction (
    InstructionId  BIGINT IDENTITY PRIMARY KEY,
    AppointmentId  BIGINT        NOT NULL REFERENCES Appointment(AppointmentId),
    ItemId         BIGINT        NULL REFERENCES AppointmentItem(ItemId),
    Sequence       INT           NOT NULL,   -- order of stops for dual / multi-move visits
    StopType       VARCHAR(15)   NOT NULL CHECK (StopType IN
                     ('TRANSFER_POINT','YARD_BLOCK','CFS','WEIGHBRIDGE','INSPECTION','BERTH','EXIT')),
    Block          VARCHAR(20)   NULL,
    LaneOrTp       VARCHAR(20)   NULL,       -- RTG lane / transfer point
    Bay            VARCHAR(10)   NULL,
    GeofenceId     INT           NULL REFERENCES Geofence(GeofenceId),  -- for navigation & auto-arrive
    TextEn         NVARCHAR(300) NULL,
    TextAr         NVARCHAR(300) NULL,
    Source         VARCHAR(10)   NOT NULL DEFAULT 'TOS' CHECK (Source IN ('TOS','PLANNER','SYSTEM')),
    Status         VARCHAR(10)   NOT NULL DEFAULT 'PENDING' CHECK (Status IN ('PENDING','ACTIVE','ARRIVED','DONE','CANCELLED')),
    IssuedAt       DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    ArrivedAt      DATETIME2(0)  NULL,
    CompletedAt    DATETIME2(0)  NULL,
    UNIQUE (AppointmentId, Sequence)
);

/* ---------- 11. Communication ---------- */

CREATE TABLE AppointmentMessage (   -- in-app chat / voice notes between driver and control room
    MessageId      BIGINT IDENTITY PRIMARY KEY,
    AppointmentId  BIGINT        NULL REFERENCES Appointment(AppointmentId),
    DriverId       INT           NOT NULL REFERENCES Driver(DriverId),
    Direction      VARCHAR(12)   NOT NULL CHECK (Direction IN ('TO_DRIVER','FROM_DRIVER')),
    SenderUserId   INT           NULL REFERENCES AppUser(UserId),   -- control room user for TO_DRIVER
    MessageType    VARCHAR(12)   NOT NULL CHECK (MessageType IN ('TEXT','VOICE','TEMPLATE','CALL_REQUEST','LOCATION')),
    TemplateCode   VARCHAR(40)   NULL,       -- predefined, auto-translated messages (e.g. 'GO_TO_TROUBLE_DESK')
    Body           NVARCHAR(1000) NULL,
    MediaUrl       VARCHAR(400)  NULL,       -- voice note in blob storage
    SentAt         DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    ReadAt         DATETIME2(0)  NULL
);
CREATE INDEX IX_Message_Appt ON AppointmentMessage(AppointmentId, SentAt);

CREATE TABLE NotificationLog (   -- outbound push / SMS / WhatsApp
    NotificationId BIGINT IDENTITY PRIMARY KEY,
    AppointmentId  BIGINT        NULL REFERENCES Appointment(AppointmentId),
    DriverId       INT           NULL REFERENCES Driver(DriverId),
    UserId         INT           NULL REFERENCES AppUser(UserId),
    Channel        VARCHAR(10)   NOT NULL CHECK (Channel IN ('PUSH','SMS','WHATSAPP','EMAIL')),
    TemplateCode   VARCHAR(40)   NOT NULL,   -- SLOT_REMINDER, CONTAINER_READY, STATUS_RED, SLOT_CLOSED ...
    Language       CHAR(2)       NOT NULL,
    Status         VARCHAR(10)   NOT NULL CHECK (Status IN ('QUEUED','SENT','DELIVERED','FAILED')),
    ProviderRef    VARCHAR(100)  NULL,
    CreatedAt      DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    SentAt         DATETIME2(0)  NULL
);

/* ---------- 12. Trucking company API ---------- */

CREATE TABLE ApiClient (
    ApiClientId   INT IDENTITY PRIMARY KEY,
    CompanyId     INT           NOT NULL REFERENCES Company(CompanyId),
    ClientId      VARCHAR(64)   NOT NULL UNIQUE,
    SecretHash    VARBINARY(64) NOT NULL,
    Scopes        VARCHAR(200)  NOT NULL,   -- e.g. 'slots:read appointments:write'
    RateLimitPerMin INT         NOT NULL DEFAULT 60,
    IsActive      BIT           NOT NULL DEFAULT 1,
    CreatedAt     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);

/* ---------- Audit ---------- */

CREATE TABLE AuditLog (
    AuditId     BIGINT IDENTITY PRIMARY KEY,
    TerminalId  INT           NULL,
    EntityName  VARCHAR(60)   NOT NULL,
    EntityKey   VARCHAR(60)   NOT NULL,
    Action      VARCHAR(10)   NOT NULL CHECK (Action IN ('INSERT','UPDATE','DELETE')),
    OldValues   NVARCHAR(MAX) NULL,   -- JSON
    NewValues   NVARCHAR(MAX) NULL,   -- JSON
    Reason      NVARCHAR(300) NULL,
    ChangedBy   INT           NOT NULL,
    ChangedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
