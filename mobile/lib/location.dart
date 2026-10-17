// Where the truck is: the phone's GPS, or in the demo build a truck driving along the sample route.

import 'package:geolocator/geolocator.dart';

abstract class LocationSource {
  Future<LocationPermission> checkPermission();
  Future<LocationPermission> requestPermission();
  Future<bool> serviceEnabled();
  Future<Position> current(LocationSettings settings);
  Stream<Position> stream(LocationSettings settings);
}

/// The phone's own location (Geolocator).
class DeviceLocation implements LocationSource {
  const DeviceLocation();

  @override
  Future<LocationPermission> checkPermission() => Geolocator.checkPermission();

  @override
  Future<LocationPermission> requestPermission() => Geolocator.requestPermission();

  @override
  Future<bool> serviceEnabled() => Geolocator.isLocationServiceEnabled();

  @override
  Future<Position> current(LocationSettings settings) => Geolocator.getCurrentPosition(locationSettings: settings);

  @override
  Stream<Position> stream(LocationSettings settings) => Geolocator.getPositionStream(locationSettings: settings);
}

/// Used by the trip tracker, the yard screen and "Share where I am".
LocationSource locations = const DeviceLocation();
