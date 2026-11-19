import 'package:flutter/material.dart';

import 'app.dart';
import 'models.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await loadDeviceName();
  runApp(const SentinelApp());
}
