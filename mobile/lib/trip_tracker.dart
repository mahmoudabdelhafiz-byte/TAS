// Position while on the way to the terminal, also with the phone locked or another app in front.
//
// Android runs it as a foreground service with a notification the driver can see ("sharing your location");
// iOS keeps location updates on in the background with the blue location indicator. It stops by itself when the
// truck checks in at the pre-gate, the trip ends, or the driver signs out. Nothing is tracked outside a trip.

import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:geolocator/geolocator.dart';

import 'api.dart';
import 'models.dart';
import 'strings.dart';

/// When to send a new position: every minute while moving, sooner after a long stretch, never more than every 15 s.
class ReportPolicy {
  const ReportPolicy({
    this.every = const Duration(seconds: 60),
    this.atLeast = const Duration(seconds: 15),
    this.farMeters = 500,
  });

  final Duration every;
  final Duration atLeast;
  final double farMeters;

  bool due({required DateTime now, DateTime? lastAt, double? movedMeters}) {
    if (lastAt == null) return true;
    final since = now.difference(lastAt);
    if (since >= every) return true;
    return since >= atLeast && (movedMeters ?? 0) >= farMeters;
  }
}

enum TrackingProblem { locationOff, permissionDenied }

class TripTracker extends ChangeNotifier {
  TripTracker(this._api, {this.policy = const ReportPolicy()});

  final DriverApi _api;
  final ReportPolicy policy;

  String? _appointmentNo;
  StreamSubscription<Position>? _subscription;
  DateTime? _lastAt;
  Position? _lastSent;
  bool _sending = false;
  bool _asked = false;

  /// The appointment being tracked, if any.
  String? get appointmentNo => _appointmentNo;

  /// The terminal's answer to the last position: status, arrival estimate, check-in.
  TripUpdate? last;

  /// The appointment [last] belongs to (kept after tracking stops, so the screen can show the check-in).
  String? lastAppointmentNo;

  TrackingProblem? problem;

  bool get active => _subscription != null;

  /// Starts sharing the position for this trip (no-op when it already runs for it).
  Future<void> follow(String appointmentNo, String lang) async {
    if (_appointmentNo == appointmentNo && _subscription != null) return;
    await stop();
    _appointmentNo = appointmentNo;
    last = null;

    var permission = await Geolocator.checkPermission();
    // Ask once per app start; the appointment list retries every minute and shouldn't keep popping the question.
    if (permission == LocationPermission.denied && !_asked) {
      _asked = true;
      permission = await Geolocator.requestPermission();
    }
    if (permission == LocationPermission.denied || permission == LocationPermission.deniedForever) {
      _appointmentNo = null;
      _setProblem(TrackingProblem.permissionDenied);
      return;
    }
    if (!await Geolocator.isLocationServiceEnabled()) {
      // The appointment list asks again every minute, so tracking starts once location is on.
      _appointmentNo = null;
      _setProblem(TrackingProblem.locationOff);
      return;
    }
    problem = null;
    notifyListeners();

    _subscription = Geolocator.getPositionStream(locationSettings: settingsFor(lang)).listen(
      _onPosition,
      onError: (Object e) {
        if (e is LocationServiceDisabledException) _setProblem(TrackingProblem.locationOff);
      },
    );
  }

  Future<void> stop() async {
    await _subscription?.cancel();
    _subscription = null;
    _appointmentNo = null;
    _lastAt = null;
    _lastSent = null;
  }

  Future<void> _onPosition(Position position) async {
    final no = _appointmentNo;
    if (no == null || _sending) return;
    final now = DateTime.now();
    final moved = _lastSent == null
        ? null
        : Geolocator.distanceBetween(_lastSent!.latitude, _lastSent!.longitude, position.latitude, position.longitude);
    if (!policy.due(now: now, lastAt: _lastAt, movedMeters: moved)) return;

    _sending = true;
    try {
      final update = await _api.reportPosition(no, position.latitude, position.longitude, position.speed >= 0 ? position.speed : null);
      _lastAt = now;
      _lastSent = position;
      last = update;
      lastAppointmentNo = no;
      problem = null;
      notifyListeners();
      // Checked in at the pre-gate or no longer on the way: stop sharing.
      if (update.status != 'EnRoute') await stop();
    } on ApiException catch (e) {
      if (e.status == 409 || e.signedOut) {
        await stop();
        notifyListeners();
      }
    } catch (_) {
      // No network: the next position tries again.
    } finally {
      _sending = false;
    }
  }

  void _setProblem(TrackingProblem p) {
    problem = p;
    notifyListeners();
  }

  /// Location settings that keep updates coming in the background.
  static LocationSettings settingsFor(String lang) {
    switch (defaultTargetPlatform) {
      case TargetPlatform.android:
        return AndroidSettings(
          accuracy: LocationAccuracy.high,
          distanceFilter: 50,
          intervalDuration: const Duration(seconds: 20),
          foregroundNotificationConfig: ForegroundNotificationConfig(
            notificationTitle: tr(lang, 'trackingTitle'),
            notificationText: tr(lang, 'trackingText'),
            notificationChannelName: tr(lang, 'trackingChannel'),
            enableWakeLock: true,
            setOngoing: true,
          ),
        );
      case TargetPlatform.iOS:
        return AppleSettings(
          accuracy: LocationAccuracy.high,
          distanceFilter: 50,
          activityType: ActivityType.automotiveNavigation,
          pauseLocationUpdatesAutomatically: false,
          showBackgroundLocationIndicator: true,
          allowBackgroundLocationUpdates: true,
        );
      default:
        return const LocationSettings(accuracy: LocationAccuracy.high, distanceFilter: 50);
    }
  }
}
