// The demo build of the driver app: no server, sample data, and a truck that drives itself.
//
//   flutter build apk --dart-define=YARDSYNCS_DEMO=true
//
// Any mobile number and any 6-digit code sign in. Today's visit drives to the terminal after "Start trip", passes the
// gate, drives to each stop on the yard map (with the spoken turns) and waits there for "Done here"; the control room
// answers messages. Nothing leaves the phone; signing out starts the demo again.

import 'dart:async';
import 'dart:convert';
import 'dart:math' as math;

import 'package:geolocator/geolocator.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'location.dart';
import 'yard.dart';

const demoMode = bool.fromEnvironment('YARDSYNCS_DEMO');

/// Turns the demo on: the API answers from [DemoWorld] and the truck's position comes from it too.
http.Client startDemo([DemoWorld? world]) {
  final w = world ?? DemoWorld();
  locations = DemoLocation(w);
  return MockClient((request) async {
    final body = request.body.isEmpty ? null : jsonDecode(request.body);
    final lang = request.headers['Accept-Language'] ?? 'ar';
    final (status, json) = w.answer(request.method, request.url.path, body is Map<String, dynamic> ? body : null, lang);
    return http.Response(json == null ? '' : jsonEncode(json), status, headers: {'content-type': 'application/json; charset=utf-8'});
  });
}

const _offset = Duration(hours: 3); // the sample terminal is in Saudi Arabia (UTC+3)
const _terminal = 'Demo Container Terminal';
const _terminalAr = 'محطة الحاويات التجريبية';

// The sample terminal: gate at the top, four blocks between the roads, the exit at the bottom right. [lon, lat]
const _gate = [39.1700, 21.4700];
const _north = 21.4690, _middle = 21.4675, _south = 21.4660;
const _west = 39.1670, _spine = 39.1700, _east = 39.1730;

List<List<double>> _rect(double x1, double y1, double x2, double y2) => [
      [x1, y1], [x2, y1], [x2, y2], [x1, y2], [x1, y1]
    ];

class _Stop {
  const _Stop(this.sequence, this.kind, this.block, this.slot, this.containers, this.en, this.ar, this.path, this.turns);

  final int sequence;
  final String kind;
  final String? block;
  final String? slot;
  final List<String> containers;
  final List<String> en;
  final List<String> ar;
  final List<List<double>> path; // from the previous stop (the gate for the first)
  final List<(int, String, String)> turns; // path point index, turn, block beside it

  List<double> get point => path.last;
}

final _stops = [
  // Gate → down the spine → right onto the middle road → stop above block E1 (drop the empty).
  _Stop(1, 'DROP', 'E1', '07.12.1', ['CAIU5512298'], ['Empty return'], ['إرجاع حاوية فارغة'], [
    _gate, [_spine, _north], [_spine, _middle], [39.1685, _middle]
  ], [(2, 'Right', 'E1')]),
  // On west → right up the west road → right along the north road → stop above block B2 (pick up the import).
  _Stop(2, 'PICKUP', 'B2', '14.03.2', ['MSCU1234566'], ['Import pickup'], ['استلام حاوية وارد'], [
    [39.1685, _middle], [_west, _middle], [_west, _north], [_spine, _north], [39.1715, _north]
  ], [(1, 'Right', 'E1'), (2, 'Right', 'A1')]),
  // On east → right down the east road → the exit.
  _Stop(3, 'EXIT', null, null, [], [], [], [
    [39.1715, _north], [_east, _north], [_east, _south]
  ], [(1, 'Right', 'B2')]),
];

const _areas = [
  ('A1', 39.1673, 21.4677, 39.1697, 21.4688),
  ('B2', 39.1703, 21.4677, 39.1727, 21.4688),
  ('E1', 39.1673, 21.4662, 39.1697, 21.4673),
  ('M1', 39.1703, 21.4662, 39.1727, 21.4673),
];

/// Metres along a path up to each point.
List<double> _along(List<List<double>> path) {
  final out = <double>[0];
  for (var i = 1; i < path.length; i++) {
    out.add(out.last + metersBetween(path[i - 1], path[i]));
  }
  return out;
}

/// The point [meters] along [path].
List<double> _at(List<List<double>> path, double meters) {
  final along = _along(path);
  if (meters <= 0) return path.first;
  for (var i = 1; i < path.length; i++) {
    if (meters <= along[i]) {
      final f = (meters - along[i - 1]) / (along[i] - along[i - 1]);
      return [path[i - 1][0] + (path[i][0] - path[i - 1][0]) * f, path[i - 1][1] + (path[i][1] - path[i - 1][1]) * f];
    }
  }
  return path.last;
}

class _Message {
  _Message(this.id, this.fromDriver, this.type, this.text, this.sentAt, {this.senderName, this.read = true});

  final int id;
  final bool fromDriver;
  final String type;
  final String text;
  final DateTime sentAt;
  final String? senderName;
  bool read;

  Map<String, dynamic> toJson() => {
        'messageId': id,
        'direction': fromDriver ? 'FromDriver' : 'ToDriver',
        'type': type,
        'text': text,
        'senderName': senderName,
        'sentAt': sentAt.toIso8601String(),
      };
}

/// The demo's state: today's visit, where the truck is, messages, the safety notice and the record.
class DemoWorld {
  DemoWorld({DateTime Function()? clock}) : _clock = clock ?? DateTime.now {
    final local = _now.add(_offset);
    // Today's visit is in the current hour at the terminal, so the trip can start straight away.
    _slotStart = DateTime.utc(local.year, local.month, local.day, local.hour).subtract(_offset);
    _gateCode = '${100000 + math.Random().nextInt(899999)}';
  }

  final DateTime Function() _clock;
  DateTime get _now => _clock().toUtc();

  static const todayNo = 'DEMO-TODAY-4KQ2M';
  static const tomorrowNo = 'DEMO-NEXT-7QW2R';
  static const yesterdayNo = 'DEMO-DONE-2PL7S';

  late final DateTime _slotStart;
  late final String _gateCode;
  String status = 'Confirmed';
  DateTime? _gateInAt;
  bool noticeConfirmed = false;
  bool inductionPassed = false;
  String? disputeText;
  final done = <int>{};
  final arrived = <int>{};
  final _messages = <_Message>[];
  int _nextMessage = 1;

  // The truck: on the way it comes down the road from the north; inside it follows the current stop's path.
  static const approachMeters = 1500.0;
  static const approachSpeed = 22.0; // m/s, shortened so the demo doesn't take long
  static const yardSpeed = 9.0;
  double _approach = 0; // metres driven towards the gate
  double _leg = 0; // metres driven on the current stop's path
  DateTime? _lastMove;

  /// Back to the start: today's visit booked, nothing read or answered.
  void reset() {
    status = 'Confirmed';
    _gateInAt = null;
    noticeConfirmed = false;
    inductionPassed = false;
    disputeText = null;
    done.clear();
    arrived.clear();
    _messages.clear();
    _approach = 0;
    _leg = 0;
    _lastMove = null;
  }

  int get _currentStop => _stops.indexWhere((s) => !done.contains(s.sequence));

  /// Moves the truck on by the time since the last call; returns its position and speed (m/s).
  (List<double>, double) move() {
    final now = _now;
    final seconds = _lastMove == null ? 0.0 : math.min(5.0, now.difference(_lastMove!).inMilliseconds / 1000);
    _lastMove = now;
    if (status == 'EnRoute') {
      _approach = math.min(approachMeters, _approach + approachSpeed * seconds);
      if (_approach >= approachMeters) {
        status = 'GateIn';
        _gateInAt = now;
        _leg = 0;
        return (_gate, 0.0);
      }
      return (_approachPoint(), approachSpeed);
    }
    if (status == 'GateIn') {
      final i = _currentStop;
      if (i < 0) return (_stops.last.point, 0.0);
      final stop = _stops[i];
      final length = _along(stop.path).last;
      _leg = math.min(length, _leg + yardSpeed * seconds);
      if (_leg >= length) {
        arrived.add(stop.sequence);
        if (stop.kind == 'EXIT') status = 'Completed';
        return (stop.point, 0.0);
      }
      return (_at(stop.path, _leg), yardSpeed);
    }
    if (status == 'Completed') return (_stops.last.point, 0.0);
    return ([_gate[0], _gate[1] + approachMeters / 111320], 0.0);
  }

  List<double> _approachPoint() => [_gate[0], _gate[1] + (approachMeters - _approach) / 111320];

  void _completeStop(int sequence) {
    if (done.add(sequence)) _leg = 0;
  }

  String _l(String lang, String en, String ar) => lang == 'ar' ? ar : en;

  Map<String, dynamic> _appointment(String no, String lang) {
    Map<String, dynamic> item(String category, String en, String ar, String container) =>
        {'category': category, 'categoryEn': en, 'categoryAr': ar, 'containerNo': container};
    final base = {
      'terminalName': _l(lang, _terminal, _terminalAr),
      'utcOffsetMinutes': 180,
      'companyName': _l(lang, 'Al Noor Transport', 'النور للنقل'),
      'truckPlate': '1234 ABJ',
      'gateCode': _gateCode,
      'unreadMessages': 0,
    };
    switch (no) {
      case todayNo:
        final remaining = status == 'EnRoute' ? (approachMeters - _approach) : null;
        return {
          ...base,
          'appointmentNo': todayNo,
          'slotStart': _slotStart.toIso8601String(),
          'slotEnd': _slotStart.add(const Duration(hours: 1)).toIso8601String(),
          'status': status,
          'trafficLight': 'Green',
          'trafficReason': _l(lang, 'Cleared: customs released, paid, ready in the yard.', 'جاهزة: مُفرج عنها جمركياً، مدفوعة، وجاهزة في الساحة.'),
          'moves': [
            item('MTY_RETURN', 'Empty return', 'إرجاع حاوية فارغة', 'CAIU5512298'),
            item('IMP_PICKUP', 'Import pickup', 'استلام حاوية وارد', 'MSCU1234566'),
          ],
          'eta': remaining == null ? null : _now.add(Duration(seconds: (remaining / 14).round())).toIso8601String(),
          'unreadMessages': _messages.where((m) => !m.fromDriver && !m.read).length,
        };
      case tomorrowNo:
        final start = _slotStart.add(const Duration(days: 1));
        final nine = DateTime.utc(start.year, start.month, start.day, 6); // 09:00 at the terminal
        return {
          ...base,
          'appointmentNo': tomorrowNo,
          'slotStart': nine.toIso8601String(),
          'slotEnd': nine.add(const Duration(hours: 1)).toIso8601String(),
          'status': 'Confirmed',
          'trafficLight': 'Amber',
          'trafficReason': _l(lang, 'Storage charges on CMAU5100028 start tomorrow: the company is paying them.',
              'رسوم التخزين على CMAU5100028 تبدأ غداً: الشركة تدفعها.'),
          'moves': [item('IMP_PICKUP', 'Import pickup', 'استلام حاوية وارد', 'CMAU5100028')],
          'gateCode': null,
        };
      default:
        final day = _slotStart.subtract(const Duration(days: 1));
        final two = DateTime.utc(day.year, day.month, day.day, 11); // 14:00 at the terminal
        return {
          ...base,
          'appointmentNo': yesterdayNo,
          'slotStart': two.toIso8601String(),
          'slotEnd': two.add(const Duration(hours: 1)).toIso8601String(),
          'status': 'Completed',
          'trafficLight': 'Green',
          'trafficReason': '',
          'moves': [item('EXP_DROP', 'Export drop-off', 'تسليم حاوية صادر', 'TGHU8812340')],
          'gateCode': null,
        };
    }
  }

  Map<String, dynamic> _yard(String lang) {
    final stops = [
      for (final s in _stops)
        {
          'sequence': s.sequence,
          'kind': s.kind,
          'block': s.block,
          'slot': s.slot,
          'containers': s.containers,
          'categoriesEn': s.en,
          'categoriesAr': s.ar,
          'moved': false,
          'point': s.point,
          'arrived': arrived.contains(s.sequence),
          'done': done.contains(s.sequence),
        }
    ];
    final legs = [
      for (final s in _stops)
        {
          'toStop': s.sequence,
          'path': s.path,
          'meters': _along(s.path).last,
          'turns': [
            for (final (index, turn, near) in s.turns)
              {'point': s.path[index], 'turn': turn, 'atMeters': _along(s.path)[index], 'near': near, 'nearIsBlock': true}
          ],
        }
    ];
    return {
      'appointmentNo': todayNo,
      'live': true,
      'ticket': {'terminalName': _l(lang, _terminal, _terminalAr), 'gateInAt': (_gateInAt ?? _now).toIso8601String(), 'lane': 'IN-1', 'truckPlate': '1234 ABJ'},
      'stops': stops,
      'legs': legs,
      'map': {
        'bounds': [39.1660, 21.4652, 39.1740, 21.4706],
        'roads': [
          [_gate, [_spine, _south]],
          [[_west, _north], [_east, _north]],
          [[_west, _middle], [_east, _middle]],
          [[_west, _south], [_east, _south]],
          [[_west, _north], [_west, _south]],
          [[_east, _north], [_east, _south]],
        ],
        'areas': [
          for (final (name, x1, y1, x2, y2) in _areas) {'name': name, 'kind': 'YARD_ZONE', 'block': name, 'ring': _rect(x1, y1, x2, y2)},
          {'name': _l(lang, 'Gate', 'البوابة'), 'kind': 'GATE', 'block': null, 'ring': _rect(39.1695, 21.4697, 39.1705, 21.4704)},
          {'name': _l(lang, 'Exit', 'المخرج'), 'kind': 'EXIT', 'block': null, 'ring': _rect(39.1726, 21.4655, 39.1734, 21.4661)},
        ],
      },
    };
  }

  static const _templates = {
    'RUNNING_LATE': ('I am running late.', 'أنا متأخر.'),
    'AT_PARKING': ('I am in the truck parking.', 'أنا في موقف الشاحنات.'),
    'TRUCK_PROBLEM': ('My truck has a problem.', 'في شاحنتي مشكلة.'),
    'CANT_FIND_CONTAINER': ('I can\'t find the container.', 'لا أجد الحاوية.'),
    'CALL_ME': ('Please call me.', 'من فضلك اتصل بي.'),
  };

  void _reply(String lang, String en, String ar) {
    Timer(const Duration(seconds: 6), () {
      _messages.add(_Message(_nextMessage++, false, 'Text', _l(lang, en, ar), _now, senderName: 'Huda Saleh', read: false));
    });
  }

  Map<String, dynamic> _record(String lang) {
    final occurred = _now.subtract(const Duration(days: 12));
    return {
      'terminals': [
        {'terminalName': _l(lang, _terminal, _terminalAr), 'points': 3, 'standing': 'Clear'}
      ],
      'incidents': [
        {
          'incidentId': 1,
          'terminalName': _l(lang, _terminal, _terminalAr),
          'appointmentNo': 'DEMO-OLD-8MZ1K',
          'kind': 'Speeding',
          'points': 3,
          'occurredAt': occurred.toIso8601String(),
          'note': _l(lang, '38 km/h on the quay road (limit 25)', '38 كم/س على طريق الرصيف (الحد 25)'),
          'disputeStatus': disputeText == null ? null : 'Open',
          'disputeText': disputeText,
          'disputeAnswer': null,
          'withdrawn': false,
          'counts': true,
          'countsUntil': occurred.add(const Duration(days: 90)).toIso8601String(),
          'canDispute': disputeText == null,
        }
      ],
    };
  }

  Map<String, dynamic> _safety(String lang) => {
        'campaigns': [
          {
            'campaignId': 1,
            'terminalName': _l(lang, _terminal, _terminalAr),
            'title': _l(lang, 'Stay in your cab under the crane', 'ابقَ في المقصورة تحت الرافعة'),
            'body': _l(lang, 'When a straddle carrier or a crane is working on your truck, stay in the cab with the door closed. Step out only when the clerk tells you.',
                'عندما تعمل الرافعة على شاحنتك ابقَ داخل المقصورة والباب مغلق، ولا تنزل إلا إذا طلب منك الموظف ذلك.'),
            'imageUrl': null,
            'requireAck': true,
            'confirmed': noticeConfirmed,
          }
        ],
        'inductions': [
          {
            'terminalId': 1,
            'terminalName': _l(lang, _terminal, _terminalAr),
            'needed': !inductionPassed,
            'validUntil': inductionPassed ? _now.add(const Duration(days: 365)).toIso8601String() : null,
            'pages': [
              {
                'title': _l(lang, 'Welcome to the terminal', 'أهلاً بك في المحطة'),
                'body': _l(lang, 'Wear your vest and safety shoes outside the cab. Follow the app and the signs to your block.',
                    'ارتدِ السترة وحذاء السلامة خارج المقصورة، واتبع التطبيق والإشارات إلى موقعك.'),
              },
              {
                'title': _l(lang, 'Speed and right of way', 'السرعة وأولوية المرور'),
                'body': _l(lang, '25 km/h everywhere. Straddle carriers and reach stackers always go first.',
                    '25 كم/س في كل مكان. الرافعات المتنقلة لها الأولوية دائماً.'),
              },
            ],
            'questions': [
              {
                'question': _l(lang, 'What is the speed limit inside the terminal?', 'ما حد السرعة داخل المحطة؟'),
                'options': [_l(lang, '40 km/h', '40 كم/س'), _l(lang, '25 km/h', '25 كم/س'), _l(lang, '60 km/h', '60 كم/س')],
              },
              {
                'question': _l(lang, 'A straddle carrier is coming. What do you do?', 'رافعة متنقلة قادمة. ماذا تفعل؟'),
                'options': [_l(lang, 'Stop and let it pass', 'أتوقف وأتركها تمر'), _l(lang, 'Drive past quickly', 'أمر بسرعة'), _l(lang, 'Sound the horn', 'أستخدم البوق')],
              },
            ],
          }
        ],
      };

  /// The API's answer: status code and JSON (null for none).
  (int, Object?) answer(String method, String path, Map<String, dynamic>? body, String lang) {
    final no = RegExp(r'/api/driver/appointments/([^/]+)').firstMatch(path)?.group(1);
    final driver = {
      'driverId': 1,
      'fullName': _l(lang, 'Abdullah Al-Harbi', 'عبدالله الحربي'),
      'mobile': '+966500000001',
      'preferredLanguage': lang,
      'companies': [_l(lang, 'Al Noor Transport', 'النور للنقل')],
    };
    switch ((method, path)) {
      case ('GET', '/api/branding'):
        return (404, null);
      case ('GET', '/api/languages'):
        return (200, <Object>[]);
      case ('GET', '/api/country'):
        return (200, {'mobileExample': '05XXXXXXXX'});
      case ('POST', '/api/driver/login/start'):
        return (202, {'sent': true});
      case ('POST', '/api/driver/login/verify'):
        final code = '${body?['code'] ?? ''}';
        if (!RegExp(r'^\d{6}$').hasMatch(code)) {
          return (400, {'detail': _l(lang, 'Type the 6 digits (any 6 digits in the demo).', 'اكتب 6 أرقام (أي 6 أرقام في النسخة التجريبية).')});
        }
        return (200, {'token': 'demo', 'driver': driver});
      case ('GET', '/api/driver/me'):
        return (200, driver);
      case ('POST', '/api/driver/logout'):
        reset();
        return (204, null);
      case ('GET', '/api/driver/appointments'):
        return (200, [_appointment(todayNo, lang), _appointment(tomorrowNo, lang), _appointment(yesterdayNo, lang)]);
      case ('GET', '/api/driver/safety'):
        return (200, _safety(lang));
      case ('GET', '/api/driver/record'):
        return (200, _record(lang));
      case ('GET', '/api/driver/message-templates'):
        return (200, [for (final e in _templates.entries) {'code': e.key, 'text': _l(lang, e.value.$1, e.value.$2)}]);
      case ('GET', '/api/driver/gate-pass'):
        return (200, {'passId': 1, 'secret': base64Encode(List.generate(32, (i) => (i * 37 + 11) % 256)), 'prefix': 'YS1', 'stepSeconds': 30, 'digits': 8});
      case ('PUT', '/api/driver/push-token'):
        return (204, null);
    }
    if (method == 'POST' && path.startsWith('/api/driver/safety/campaigns/')) {
      noticeConfirmed = true;
      return (204, null);
    }
    if (method == 'POST' && path.startsWith('/api/driver/safety/inductions/')) {
      final answers = ((body?['answers'] as List?) ?? const []).map((e) => (e as num).toInt()).toList();
      const right = [1, 0];
      final wrong = [for (var i = 0; i < right.length; i++) if (i >= answers.length || answers[i] != right[i]) i];
      inductionPassed = wrong.isEmpty;
      return (200, {
        'data': {'passed': wrong.isEmpty, 'wrong': wrong, 'validUntil': wrong.isEmpty ? _now.add(const Duration(days: 365)).toIso8601String() : null}
      });
    }
    if (method == 'POST' && path.startsWith('/api/driver/incidents/')) {
      disputeText = '${body?['text'] ?? ''}';
      return (200, {'data': _record(lang)});
    }
    if (no == null) return (404, {'detail': _l(lang, 'Not in the demo.', 'غير متاح في النسخة التجريبية.')});
    if (no != todayNo) {
      if (path.endsWith('/messages') && method == 'GET') return (200, {'data': <Object>[]});
      return (409, {'detail': _l(lang, 'Only today\'s visit runs in the demo.', 'زيارة اليوم فقط تعمل في النسخة التجريبية.')});
    }
    if (path.endsWith('/start-trip') && method == 'POST') {
      if (status == 'Confirmed') {
        status = 'EnRoute';
        _approach = 0;
        _lastMove = null;
      }
      return (200, _appointment(todayNo, lang));
    }
    if (path.endsWith('/position') && method == 'POST') {
      move();
      final i = _currentStop;
      final atStop = status == 'GateIn' && i >= 0 && arrived.contains(_stops[i].sequence) ? _stops[i].sequence : null;
      final remaining = status == 'EnRoute' ? approachMeters - _approach : 0.0;
      return (200, {
        'status': status,
        'eta': status == 'EnRoute' ? _now.add(Duration(seconds: (remaining / 14).round())).toIso8601String() : null,
        'distanceKm': status == 'EnRoute' ? remaining / 1000 : null,
        'checkedIn': status == 'GateIn',
        'atStop': atStop,
      });
    }
    if (path.contains('/stops/') && path.endsWith('/done') && method == 'POST') {
      _completeStop(int.parse(path.split('/stops/').last.split('/').first));
      return (204, null);
    }
    if (path.endsWith('/yard') && method == 'GET') {
      if (status != 'GateIn' && status != 'Completed') {
        return (409, {'detail': _l(lang, 'Directions start after the gate.', 'تبدأ الإرشادات بعد البوابة.')});
      }
      return (200, _yard(lang));
    }
    if (path.endsWith('/messages') && method == 'GET') {
      for (final m in _messages) {
        m.read = true;
      }
      return (200, {'data': [for (final m in _messages) m.toJson()]});
    }
    if (path.endsWith('/messages') && method == 'POST') {
      final code = body?['templateCode'] as String?;
      final lat = body?['latitude'];
      final _Message m;
      if (lat != null) {
        m = _Message(_nextMessage++, true, 'Location', _l(lang, 'Here I am.', 'هذا موقعي.'), _now);
        _reply(lang, 'Got your location, thank you.', 'وصلنا موقعك، شكراً.');
      } else if (code == 'CALL_ME') {
        m = _Message(_nextMessage++, true, 'CallRequest', _l(lang, _templates[code]!.$1, _templates[code]!.$2), _now);
        _reply(lang, 'Calling you in a minute.', 'سنتصل بك خلال دقيقة.');
      } else if (code != null && _templates.containsKey(code)) {
        m = _Message(_nextMessage++, true, 'Template', _l(lang, _templates[code]!.$1, _templates[code]!.$2), _now);
        _reply(lang, 'Received. Please wait in the truck parking; we will tell you when to come.',
            'وصلت رسالتك. انتظر في موقف الشاحنات وسنخبرك متى تأتي.');
      } else {
        m = _Message(_nextMessage++, true, 'Text', '${body?['text'] ?? ''}', _now);
        _reply(lang, 'Received, thank you.', 'وصلت، شكراً.');
      }
      _messages.add(m);
      return (200, {'data': m.toJson()});
    }
    return (404, {'detail': _l(lang, 'Not in the demo.', 'غير متاح في النسخة التجريبية.')});
  }
}

/// The truck's position in the demo: always allowed, moved by [DemoWorld] once a second.
class DemoLocation implements LocationSource {
  DemoLocation(this.world);

  final DemoWorld world;

  Position _position() {
    final (p, speed) = world.move();
    return Position(
      longitude: p[0],
      latitude: p[1],
      timestamp: DateTime.now(),
      accuracy: 5,
      altitude: 0,
      altitudeAccuracy: 0,
      heading: 0,
      headingAccuracy: 0,
      speed: speed,
      speedAccuracy: 0,
    );
  }

  @override
  Future<LocationPermission> checkPermission() async => LocationPermission.always;

  @override
  Future<LocationPermission> requestPermission() async => LocationPermission.always;

  @override
  Future<bool> serviceEnabled() async => true;

  @override
  Future<Position> current(LocationSettings settings) async => _position();

  @override
  Stream<Position> stream(LocationSettings settings) =>
      Stream<Position>.periodic(const Duration(seconds: 1), (_) => _position());
}
