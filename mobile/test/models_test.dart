import 'package:flutter_test/flutter_test.dart';
import 'package:yardsyncs_mobile/models.dart';

Map<String, dynamic> sample({String start = '2026-10-20T07:00:00+00:00', String end = '2026-10-20T08:00:00+00:00'}) => {
      'appointmentNo': 'DEMO-261020-7KQ4M',
      'terminalId': 1,
      'terminalName': 'Demo Container Terminal',
      'timeZone': 'Asia/Riyadh',
      'utcOffsetMinutes': 180,
      'slotStart': start,
      'slotEnd': end,
      'status': 'Confirmed',
      'trafficLight': 'Red',
      'trafficReason': 'Customs hold on MSCU7654329',
      'companyName': 'Al Noor Transport',
      'truckPlate': '1234 ABJ',
      'moves': [
        {'category': 'IMP_PICKUP', 'categoryEn': 'Import pickup', 'categoryAr': 'استلام حاوية وارد', 'containerNo': 'MSCU7654329'},
        {'category': 'MTY_PICKUP', 'categoryEn': 'Empty pickup', 'categoryAr': 'استلام حاوية فارغة', 'bookingRef': 'BKG1001'},
      ],
      'gateCode': 'qr-token',
    };

void main() {
  test('parses the API answer and shows terminal time', () {
    final a = DriverAppointment.fromJson(sample());
    expect(a.slotStart.isUtc, isTrue);
    expect(hours(a), '10:00–11:00');
    expect(a.moves.map((m) => m.reference), ['MSCU7654329', 'BKG1001']);
    expect(a.moves.first.name('ar'), 'استلام حاوية وارد');
    expect(a.moves.first.name('ur'), 'Import pickup');
    expect(a.canStartTrip, isTrue);
    expect(a.isActive, isTrue);
  });

  test('midnight shows as 24:00; today and tomorrow are judged at the terminal', () {
    final late = DriverAppointment.fromJson(sample(start: '2026-10-20T20:00:00Z', end: '2026-10-20T21:00:00Z'));
    expect(hours(late), '23:00–24:00');

    // 22:30 UTC on 19 Oct is already 20 Oct at the terminal (UTC+3).
    final a = DriverAppointment.fromJson(sample());
    expect(dayWord(a, DateTime.utc(2026, 10, 19, 22, 30)), 'today');
    expect(dayWord(a, DateTime.utc(2026, 10, 19, 12)), 'tomorrow');
    expect(dayWord(a, DateTime.utc(2026, 10, 17, 12)), isNull);
    expect(shortDate(a), '20/10');
  });

  test('arrival estimate in terminal time', () {
    final json = sample()..['status'] = 'EnRoute'..['eta'] = '2026-10-20T06:42:00+00:00';
    final a = DriverAppointment.fromJson(json);
    expect(a.etaClock(), '09:42');
    expect(a.copyWith(status: 'Arrived').status, 'Arrived');
    expect(DriverAppointment.fromJson(sample()).etaClock(), isNull);
  });

  test('position answer inside the terminal names the stop reached', () {
    final u = TripUpdate.fromJson({'status': 'GateIn', 'eta': null, 'distanceKm': null, 'checkedIn': false, 'areas': ['YARD_ZONE'], 'atStop': 1});
    expect(u.atStop, 1);
    expect(TripUpdate.fromJson({'status': 'EnRoute', 'checkedIn': false}).atStop, isNull);
  });

  test('messages: unread count, who wrote and call requests', () {
    expect(DriverAppointment.fromJson(sample()).unreadMessages, 0);
    expect(DriverAppointment.fromJson({...sample(), 'unreadMessages': 2}).unreadMessages, 2);

    final m = ChatMessage.fromJson({
      'messageId': 7,
      'direction': 'FromDriver',
      'type': 'CallRequest',
      'templateCode': 'CALL_ME',
      'text': 'براہ کرم مجھے کال کریں۔',
      'senderName': null,
      'sentAt': '2026-10-20T07:05:00+00:00',
      'readAt': null,
    });
    expect(m.fromDriver, isTrue);
    expect(m.callRequest, isTrue);
    expect(m.sentAt.isUtc, isTrue);
    expect(ChatMessage.fromJson({'messageId': 8, 'direction': 'ToDriver', 'type': 'Template', 'text': 'x', 'sentAt': '2026-10-20T07:06:00Z'}).fromDriver, isFalse);
    expect(ChatMessage.fromJson({'messageId': 9, 'direction': 'FromDriver', 'type': 'Location', 'text': 'x', 'sentAt': '2026-10-20T07:07:00Z'}).location, isTrue);
  });
}
