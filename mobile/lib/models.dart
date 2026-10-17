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
  const ChatMessage({required this.id, required this.fromDriver, required this.text, required this.sentAt, this.callRequest = false, this.senderName});

  final int id;
  final bool fromDriver;
  final String text;
  final DateTime sentAt; // UTC
  final bool callRequest;
  final String? senderName;

  factory ChatMessage.fromJson(Map<String, dynamic> json) => ChatMessage(
        id: (json['messageId'] as num).toInt(),
        fromDriver: json['direction'] == 'FromDriver',
        text: (json['text'] as String?) ?? '',
        sentAt: DateTime.parse(json['sentAt'] as String).toUtc(),
        callRequest: json['type'] == 'CallRequest',
        senderName: json['senderName'] as String?,
      );
}

/// A ready-made message the driver can send with one tap.
class QuickMessage {
  const QuickMessage({required this.code, required this.text});

  final String code;
  final String text;

  factory QuickMessage.fromJson(Map<String, dynamic> json) => QuickMessage(code: json['code'] as String, text: (json['text'] as String?) ?? '');
}
