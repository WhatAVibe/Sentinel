import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/flipper_bridge.dart';
import '../theme.dart';
import '../widgets.dart';

class FlipperPage extends StatelessWidget {
  const FlipperPage({super.key});

  @override
  Widget build(BuildContext context) {
    final bridge = context.watch<FlipperBridge>();

    return Scaffold(
      backgroundColor: C.bg,
      appBar: AppBar(
        backgroundColor: C.bg,
        title: const Text('Flipper bridge'),
      ),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 28),
        children: [
          CardBox(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    const Icon(Icons.watch_rounded, color: C.accent, size: 20),
                    const SizedBox(width: 10),
                    const Text('Phone as the network',
                        style: TextStyle(fontSize: 14, fontWeight: FontWeight.w600)),
                    const Spacer(),
                    Pill(
                      bridge.connected
                          ? 'LINKED'
                          : (bridge.connecting || bridge.reconnecting ? 'RETRY' : 'OFF'),
                      color: bridge.connected
                          ? C.success
                          : (bridge.connecting || bridge.reconnecting ? C.warning : C.muted),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                const Text(
                  'The Flipper has no WiFi. Connect it to this phone over Bluetooth and Sentinel will '
                  'relay its commands; your phone keeps the token and TOTP.',
                  style: TextStyle(fontSize: 11, color: C.muted, height: 1.4),
                ),
                const SizedBox(height: 10),
                const Text(
                  '1. Flipper: Sentinel → Link: Bluetooth.\n'
                  '2. Tap Scan, pick the Flipper, and accept the pairing prompt.\n'
                  '3. If the Flipper shows a PIN, type it on the phone.',
                  style: TextStyle(fontSize: 11, color: C.muted, height: 1.45),
                ),
              ],
            ),
          ),
          if (bridge.error != null) ...[
            const SizedBox(height: 10),
            CardBox(
              padding: const EdgeInsets.all(12),
              child: Row(
                children: [
                  const Icon(Icons.error_outline_rounded, color: C.warning, size: 17),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(bridge.error!,
                        style: const TextStyle(fontSize: 11, color: C.warning)),
                  ),
                ],
              ),
            ),
          ],
          if (bridge.connected || bridge.connecting || bridge.reconnecting) ...[
            const SizedBox(height: 12),
            CardBox(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    bridge.connected
                        ? 'Connected'
                        : (bridge.reconnecting ? 'Reconnecting…' : 'Connecting…'),
                    style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600),
                  ),
                  const SizedBox(height: 4),
                  Text(
                    bridge.statusText ?? 'Waiting for the Flipper',
                    style: const TextStyle(fontSize: 11, color: C.muted),
                  ),
                  const SizedBox(height: 12),
                  Row(
                    children: [
                      Expanded(
                        child: OutlinedButton.icon(
                          onPressed: bridge.connected ? bridge.sendState : bridge.reconnectNow,
                          icon: Icon(
                            bridge.connected
                                ? Icons.refresh_rounded
                                : Icons.bluetooth_connected_rounded,
                            size: 16,
                          ),
                          label: Text(bridge.connected ? 'Send status' : 'Reconnect'),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: OutlinedButton.icon(
                          onPressed: bridge.disconnect,
                          icon: const Icon(Icons.link_off_rounded, size: 16),
                          label: const Text('Disconnect'),
                        ),
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ] else ...[
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: FilledButton.icon(
                    onPressed: bridge.scanning ? bridge.stopScan : bridge.scan,
                    icon: bridge.scanning
                        ? const SizedBox(
                            width: 14,
                            height: 14,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : const Icon(Icons.bluetooth_searching_rounded, size: 17),
                    label: Text(bridge.scanning ? 'Stop scan' : 'Scan for Flipper'),
                  ),
                ),
              ],
            ),
            if (bridge.results.isNotEmpty) ...[
              const SectionHeader('Nearby devices'),
              for (final result in bridge.results)
                Padding(
                  padding: const EdgeInsets.only(bottom: 8),
                  child: CardBox(
                    padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                    child: InkWell(
                      onTap: () => bridge.connect(result.device),
                      child: Row(
                        children: [
                          Icon(
                            FlipperBridge.isFlipper(result.device)
                                ? Icons.watch_rounded
                                : Icons.bluetooth_rounded,
                            size: 18,
                            color: FlipperBridge.isFlipper(result.device) ? C.accent : C.muted,
                          ),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(
                                  result.device.advName.isEmpty
                                      ? result.device.remoteId.str
                                      : result.device.advName,
                                  style: const TextStyle(fontSize: 13),
                                ),
                                Text(
                                  result.device.remoteId.str,
                                  style: const TextStyle(fontSize: 9.5, color: C.muted),
                                ),
                              ],
                            ),
                          ),
                          Text('${result.rssi} dBm',
                              style: const TextStyle(fontSize: 10, color: C.muted)),
                        ],
                      ),
                    ),
                  ),
                ),
            ] else if (bridge.scanning) ...[
              const SizedBox(height: 18),
              const Center(
                child: Text('Listening for Bluetooth devices…',
                    style: TextStyle(fontSize: 11, color: C.muted)),
              ),
            ],
          ],
          const SizedBox(height: 16),
          const Text(
            'Bluetooth range is roughly 10 m. The Flipper must stay connected to this phone while it '
            'controls the PC.',
            style: TextStyle(fontSize: 10, color: C.muted, height: 1.4),
          ),
        ],
      ),
    );
  }
}
