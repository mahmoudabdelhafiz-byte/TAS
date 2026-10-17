// Shapes returned by the Yardsyncs driver API (/api/driver/...).

class DriverProfile {
  const DriverProfile({required this.driverId, required this.fullName, required this.mobile, required this.language, required this.companies});

  final int driverId;
  final String fullName;
  final String mobile;
  final String language;
  final List<String> companies;

  factory DriverProfile.fromJson(Map<String, dynamic> json) => DriverProfile(
        driverId: json['driverId'] as int,
        fullName: json['fullName'] as String,
        mobile: json['mobile'] as String,
        language: (json['preferredLanguage'] as String?) ?? 'ar',
        companies: ((json['companies'] as List?) ?? const []).cast<String>(),
      );
}

class Move {
  const Move({required this.category, required this.categoryEn, required this.categoryAr, this.containerNo, this.bookingRef, this.cargoRef});

  final String category;
  final String categoryEn;
  final String categoryAr;
  final String? containerNo;
  final String? bookingRef;
  final String? cargoRef;

  /// What the driver reads: the container number, else the booking or cargo reference.
  String get reference => containerNo ?? bookingRef ?? cargoRef ?? category;

  String name(String lang) => lang == 'ar' ? categoryAr : categoryEn;

  factory Move.fromJson(Map<String, dynamic> json) => Move(
        category: json['category'] as String,
        categoryEn: json['categoryEn'] as String,
        categoryAr: json['categoryAr'] as String,
        containerNo: json['containerNo'] as String?,
        bookingRef: json['bookingRef'] as String?,
        cargoRef: json['cargoRef'] as String?,
      );
}

class DriverAppointment {
  const DriverAppointment({
    required this.appointmentNo,
    required this.terminalName,
    required this.utcOffsetMinutes,
    required this.slotStart,
    required this.slotEnd,
    required this.status,
    required this.trafficLight,
    required this.trafficReason,
    required this.companyName,
    required this.truckPlate,
    required this.moves,
    required this.gateCode,
    this.eta,
    this.unreadMessages = 0,
  });

  final String appointmentNo;
  final String terminalName;
  final int utcOffsetMinutes;
  final DateTime slotStart; // UTC
  final DateTime slotEnd; // UTC
  final String status;
  final String trafficLight;
  final String trafficReason;
  final String companyName;
  final String? truckPlate;
  final List<Move> moves;
  final String? gateCode;
  final DateTime? eta; // UTC

  /// Messages from the control room not opened yet.
  final int unreadMessages;

  DriverAppointment copyWith({String? status, DateTime? eta, int? unreadMessages}) => DriverAppointment(
        appointmentNo: appointmentNo,
        terminalName: terminalName,
        utcOffsetMinutes: utcOffsetMinutes,
        slotStart: slotStart,
        slotEnd: slotEnd,
        status: status ?? this.status,
        trafficLight: trafficLight,
        trafficReason: trafficReason,
        companyName: companyName,
        truckPlate: truckPlate,
        moves: moves,
        gateCode: gateCode,
        eta: eta,
        unreadMessages: unreadMessages ?? this.unreadMessages,
      );

  bool get canStartTrip => status == 'Confirmed' || status == 'EnRoute';
  bool get isActive => status == 'Confirmed' || status == 'EnRoute' || status == 'Arrived' || status == 'GateIn';

  /// Slot start / end as wall-clock time at the terminal.
  DateTime get localStart => slotStart.add(Duration(minutes: utcOffsetMinutes));
  DateTime get localEnd => slotEnd.add(Duration(minutes: utcOffsetMinutes));

  factory DriverAppointment.fromJson(Map<String, dynamic> json) => DriverAppointment(
        appointmentNo: json['appointmentNo'] as String,
        terminalName: json['terminalName'] as String,
        utcOffsetMinutes: (json['utcOffsetMinutes'] as num?)?.toInt() ?? 0,
        slotStart: DateTime.parse(json['slotStart'] as String).toUtc(),
        slotEnd: DateTime.parse(json['slotEnd'] as String).toUtc(),
        status: json['status'] as String,
        trafficLight: json['trafficLight'] as String,
        trafficReason: (json['trafficReason'] as String?) ?? '',
        companyName: json['companyName'] as String,
        truckPlate: json['truckPlate'] as String?,
        moves: ((json['moves'] as List?) ?? const []).map((m) => Move.fromJson(m as Map<String, dynamic>)).toList(),
        gateCode: json['gateCode'] as String?,
        eta: json['eta'] == null ? null : DateTime.parse(json['eta'] as String).toUtc(),
        unreadMessages: (json['unreadMessages'] as num?)?.toInt() ?? 0,
      );

  /// "10:42" in terminal time.
  String? etaClock() {
    final e = eta?.add(Duration(minutes: utcOffsetMinutes));
    return e == null ? null : '${two(e.hour)}:${two(e.minute)}';
  }
}

String two(int n) => n.toString().padLeft(2, '0');

/// "10:00–11:00" in terminal time.
String hours(DriverAppointment a) {
  final s = a.localStart;
  final e = a.localEnd;
  final end = e.hour == 0 && e.minute == 0 ? '24:00' : '${two(e.hour)}:${two(e.minute)}';
  return '${two(s.hour)}:${two(s.minute)}–$end';
}

/// 'today', 'tomorrow' or null (then show the date) — compared at the terminal.
String? dayWord(DriverAppointment a, DateTime nowUtc) {
  final today = nowUtc.add(Duration(minutes: a.utcOffsetMinutes));
  final day = DateTime.utc(a.localStart.year, a.localStart.month, a.localStart.day);
  final d0 = DateTime.utc(today.year, today.month, today.day);
  final diff = day.difference(d0).inDays;
  if (diff == 0) return 'today';
  if (diff == 1) return 'tomorrow';
  return null;
}

/// "20/10" for days other than today / tomorrow.
String shortDate(DriverAppointment a) => '${two(a.localStart.day)}/${two(a.localStart.month)}';

/// What the server answers to a position while on the way.
class TripUpdate {
  const TripUpdate({required this.status, required this.eta, required this.distanceKm, required this.checkedIn, this.atStop});

  final String status;
  final DateTime? eta;
  final double? distanceKm;
  final bool checkedIn;

  /// Inside the terminal: the stop whose block the truck just reached.
  final int? atStop;

  factory TripUpdate.fromJson(Map<String, dynamic> json) => TripUpdate(
        status: json['status'] as String,
        eta: json['eta'] == null ? null : DateTime.parse(json['eta'] as String).toUtc(),
        distanceKm: (json['distanceKm'] as num?)?.toDouble(),
        checkedIn: json['checkedIn'] as bool? ?? false,
        atStop: (json['atStop'] as num?)?.toInt(),
      );
}

/// A message between the driver and the control room (ready-made ones come in the app's language).
class ChatMessage {
  const ChatMessage({required this.id, required this.fromDriver, required this.text, required this.sentAt, this.callRequest = false, this.senderName, this.location = false});

  final int id;
  final bool fromDriver;
  final String text;
  final DateTime sentAt; // UTC
  final bool callRequest;
  final String? senderName;

  /// The driver shared where the truck was.
  final bool location;

  factory ChatMessage.fromJson(Map<String, dynamic> json) => ChatMessage(
        id: (json['messageId'] as num).toInt(),
        fromDriver: json['direction'] == 'FromDriver',
        text: (json['text'] as String?) ?? '',
        sentAt: DateTime.parse(json['sentAt'] as String).toUtc(),
        callRequest: json['type'] == 'CallRequest',
        senderName: json['senderName'] as String?,
        location: json['type'] == 'Location',
      );
}

/// A ready-made message the driver can send with one tap.
class QuickMessage {
  const QuickMessage({required this.code, required this.text});

  final String code;
  final String text;

  factory QuickMessage.fromJson(Map<String, dynamic> json) => QuickMessage(code: json['code'] as String, text: (json['text'] as String?) ?? '');
}

/// An entry on the driver's record at a terminal.
class RecordEntry {
  const RecordEntry({
    required this.id,
    required this.terminalName,
    this.appointmentNo,
    required this.kind,
    required this.points,
    required this.occurredAt,
    this.note,
    this.disputeStatus,
    this.disputeText,
    this.disputeAnswer,
    this.withdrawn = false,
    this.counts = true,
    required this.countsUntil,
    this.canDispute = false,
  });

  final int id;
  final String terminalName;
  final String? appointmentNo;
  final String kind; // Speeding, UnsafeDriving, NoPpe, WrongArea, Damage, Abusive, NoShow, Other
  final int points;
  final DateTime occurredAt; // UTC
  final String? note;
  final String? disputeStatus; // Open, Accepted, Rejected
  final String? disputeText;
  final String? disputeAnswer;
  final bool withdrawn;
  final bool counts;
  final DateTime countsUntil; // UTC
  final bool canDispute;

  factory RecordEntry.fromJson(Map<String, dynamic> json) => RecordEntry(
        id: (json['incidentId'] as num).toInt(),
        terminalName: (json['terminalName'] as String?) ?? '',
        appointmentNo: json['appointmentNo'] as String?,
        kind: (json['kind'] as String?) ?? 'Other',
        points: (json['points'] as num?)?.toInt() ?? 0,
        occurredAt: DateTime.parse(json['occurredAt'] as String).toUtc(),
        note: json['note'] as String?,
        disputeStatus: json['disputeStatus'] as String?,
        disputeText: json['disputeText'] as String?,
        disputeAnswer: json['disputeAnswer'] as String?,
        withdrawn: json['withdrawn'] as bool? ?? false,
        counts: json['counts'] as bool? ?? false,
        countsUntil: DateTime.parse(json['countsUntil'] as String).toUtc(),
        canDispute: json['canDispute'] as bool? ?? false,
      );
}

/// Points at one terminal and where they stand (Clear, Warning, OverLimit).
class TerminalPoints {
  const TerminalPoints({required this.terminalName, required this.points, required this.standing});

  final String terminalName;
  final int points;
  final String standing;

  factory TerminalPoints.fromJson(Map<String, dynamic> json) => TerminalPoints(
        terminalName: (json['terminalName'] as String?) ?? '',
        points: (json['points'] as num?)?.toInt() ?? 0,
        standing: (json['standing'] as String?) ?? 'Clear',
      );
}

class MyRecord {
  const MyRecord({required this.terminals, required this.entries});

  final List<TerminalPoints> terminals;
  final List<RecordEntry> entries;

  factory MyRecord.fromJson(Map<String, dynamic> json) => MyRecord(
        terminals: ((json['terminals'] as List?) ?? const []).map((e) => TerminalPoints.fromJson(e as Map<String, dynamic>)).toList(),
        entries: ((json['incidents'] as List?) ?? const []).map((e) => RecordEntry.fromJson(e as Map<String, dynamic>)).toList(),
      );
}

/// A safety notice from a terminal, in the app's language.
class SafetyNotice {
  const SafetyNotice({required this.id, required this.terminalName, required this.title, required this.body, this.imageUrl, this.requireAck = true, this.confirmed = false});

  final int id;
  final String terminalName;
  final String title;
  final String body;
  final String? imageUrl;
  final bool requireAck;
  final bool confirmed;

  factory SafetyNotice.fromJson(Map<String, dynamic> json) => SafetyNotice(
        id: (json['campaignId'] as num).toInt(),
        terminalName: (json['terminalName'] as String?) ?? '',
        title: (json['title'] as String?) ?? '',
        body: (json['body'] as String?) ?? '',
        imageUrl: json['imageUrl'] as String?,
        requireAck: json['requireAck'] as bool? ?? true,
        confirmed: json['confirmed'] as bool? ?? false,
      );
}

/// A terminal's safety induction: pages to read, then questions (no answers), and whether the driver needs it.
class InductionCourse {
  const InductionCourse({required this.terminalId, required this.terminalName, required this.pages, required this.questions, this.validUntil, this.needed = true});

  final int terminalId;
  final String terminalName;
  final List<(String, String)> pages; // title, text
  final List<(String, List<String>)> questions; // question, answers
  final DateTime? validUntil; // UTC
  final bool needed;

  factory InductionCourse.fromJson(Map<String, dynamic> json) => InductionCourse(
        terminalId: (json['terminalId'] as num).toInt(),
        terminalName: (json['terminalName'] as String?) ?? '',
        pages: ((json['pages'] as List?) ?? const [])
            .map((p) => ((p as Map<String, dynamic>)['title'] as String? ?? '', p['body'] as String? ?? ''))
            .toList(),
        questions: ((json['questions'] as List?) ?? const [])
            .map((q) => ((q as Map<String, dynamic>)['question'] as String? ?? '', ((q['options'] as List?) ?? const []).cast<String>()))
            .toList(),
        validUntil: json['validUntil'] == null ? null : DateTime.parse(json['validUntil'] as String).toUtc(),
        needed: json['needed'] as bool? ?? true,
      );
}

class SafetyFeed {
  const SafetyFeed({required this.notices, required this.inductions});

  final List<SafetyNotice> notices;
  final List<InductionCourse> inductions;

  factory SafetyFeed.fromJson(Map<String, dynamic> json) => SafetyFeed(
        notices: ((json['campaigns'] as List?) ?? const []).map((e) => SafetyNotice.fromJson(e as Map<String, dynamic>)).toList(),
        inductions: ((json['inductions'] as List?) ?? const []).map((e) => InductionCourse.fromJson(e as Map<String, dynamic>)).toList(),
      );
}

/// The marked answers: passed, or which questions to read again.
class InductionResult {
  const InductionResult({required this.passed, required this.wrong, this.validUntil});

  final bool passed;
  final List<int> wrong;
  final DateTime? validUntil;

  factory InductionResult.fromJson(Map<String, dynamic> json) => InductionResult(
        passed: json['passed'] as bool? ?? false,
        wrong: ((json['wrong'] as List?) ?? const []).map((e) => (e as num).toInt()).toList(),
        validUntil: json['validUntil'] == null ? null : DateTime.parse(json['validUntil'] as String).toUtc(),
      );
}
