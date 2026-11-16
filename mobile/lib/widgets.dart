import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/app_state.dart';
import '../core/totp.dart';
import '../models.dart';
import '../theme.dart';

class BrandLogo extends StatelessWidget {
  final double size;
  final double radius;

  const BrandLogo({super.key, this.size = 56, this.radius = 16});

  @override
  Widget build(BuildContext context) {
    return ClipRRect(
      borderRadius: BorderRadius.circular(radius),
      child: Image.asset(
        'assets/logo.png',
        width: size,
        height: size,
        fit: BoxFit.cover,
        filterQuality: FilterQuality.high,
      ),
    );
  }
}

class CardBox extends StatelessWidget {  final Widget child;
  final EdgeInsets padding;
  final Color? color;
  final EdgeInsets margin;

  const CardBox({
    super.key,
    required this.child,
    this.padding = const EdgeInsets.all(18),
    this.color,
    this.margin = EdgeInsets.zero,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: margin,
      padding: padding,
      decoration: BoxDecoration(
        color: color ?? C.surface,
        borderRadius: BorderRadius.circular(18),
        border: Border.all(color: C.border),
      ),
      child: child,
    );
  }
}

class Pill extends StatelessWidget {  final String text;
  final Color color;
  final IconData? icon;

  const Pill(this.text, {super.key, this.color = C.muted, this.icon});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(20),
        border: Border.all(color: color.withValues(alpha: 0.35)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[
            Icon(icon, size: 12, color: color),
            const SizedBox(width: 5),
          ] else ...[
            Container(
              width: 6,
              height: 6,
              decoration: BoxDecoration(color: color, shape: BoxShape.circle),
            ),
            const SizedBox(width: 6),
          ],
          Text(text, style: TextStyle(color: color, fontSize: 11, fontWeight: FontWeight.w600)),
        ],
      ),
    );
  }
}

class ActionTile extends StatelessWidget {
  final IconData icon;
  final String title;
  final String subtitle;
  final Color color;
  final VoidCallback? onTap;
  final bool busy;

  const ActionTile({
    super.key,
    required this.icon,
    required this.title,
    required this.subtitle,
    this.color = C.accent,
    this.onTap,
    this.busy = false,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: busy ? null : onTap,
        borderRadius: BorderRadius.circular(16),
        child: Ink(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: C.elevated,
            borderRadius: BorderRadius.circular(16),
            border: Border.all(color: C.border),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(icon, color: color, size: 20),
                  const Spacer(),
                  if (busy)
                    const SizedBox(
                      width: 12,
                      height: 12,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    ),
                ],
              ),
              const SizedBox(height: 12),
              Text(title, style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600)),
              const SizedBox(height: 3),
              Text(subtitle, style: const TextStyle(fontSize: 11, color: C.muted)),
            ],
          ),
        ),
      ),
    );
  }
}

class EmptyState extends StatelessWidget {
  final IconData icon;
  final String title;
  final String subtitle;

  const EmptyState({super.key, required this.icon, required this.title, required this.subtitle});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 40, color: C.muted.withValues(alpha: 0.6)),
            const SizedBox(height: 14),
            Text(title, style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w600)),
            const SizedBox(height: 6),
            Text(subtitle, textAlign: TextAlign.center, style: const TextStyle(fontSize: 12, color: C.muted)),
          ],
        ),
      ),
    );
  }
}

class StatChip extends StatelessWidget {
  final String label;
  final String value;

  const StatChip({super.key, required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 9),
      decoration: BoxDecoration(
        color: C.elevated,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: C.border),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label, style: const TextStyle(fontSize: 10, color: C.muted)),
          const SizedBox(height: 3),
          Text(value, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
        ],
      ),
    );
  }
}

class ConnectionPill extends StatelessWidget {
  final AppState state;

  const ConnectionPill({super.key, required this.state});

  @override
  Widget build(BuildContext context) {
    final direct = state.session?.isDirect ?? false;
    final online = state.connected;
    final color = online ? C.success : C.warning;
    final label = online ? (direct ? 'DIRECT' : 'LIVE') : 'OFFLINE';
    return Pill(label, color: color, icon: online ? Icons.bolt_rounded : Icons.cloud_off_rounded);
  }
}

Future<bool> confirmAction(
  BuildContext context, {
  required String title,
  required String message,
  String confirmLabel = 'Confirm',
  bool danger = false,
}) async {
  final result = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      backgroundColor: C.surface,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
      title: Text(title, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w600)),
      content: Text(message, style: const TextStyle(fontSize: 13, color: C.muted)),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancel')),
        FilledButton(
          style: FilledButton.styleFrom(
            backgroundColor: danger ? C.danger : C.accent,
            foregroundColor: danger ? Colors.black : Colors.white,
          ),
          onPressed: () => Navigator.pop(context, true),
          child: Text(confirmLabel),
        ),
      ],
    ),
  );
  return result ?? false;
}

Future<String?> showCodeSheet(
  BuildContext context, {
  required String title,
  required String actionLabel,
}) {
  return showModalBottomSheet<String>(
    context: context,
    isScrollControlled: true,
    backgroundColor: C.surface,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(22)),
    ),
    builder: (context) => Padding(
      padding: EdgeInsets.only(bottom: MediaQuery.of(context).viewInsets.bottom),
      child: _CodeSheet(title: title, actionLabel: actionLabel),
    ),
  );
}

class _CodeSheet extends StatefulWidget {
  final String title;
  final String actionLabel;

  const _CodeSheet({required this.title, required this.actionLabel});

  @override
  State<_CodeSheet> createState() => _CodeSheetState();
}

class _CodeSheetState extends State<_CodeSheet> {
  late final TextEditingController _controller;
  late final Timer _timer;
  int _seconds = totpSecondsRemaining();
  bool _edited = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final state = context.read<AppState>();
    _controller = TextEditingController(text: state.liveCode);
    _timer = Timer.periodic(const Duration(seconds: 1), (_) {
      final seconds = totpSecondsRemaining();
      if (seconds == _seconds) return;
      _seconds = seconds;
      if (!_edited) {
        final code = context.read<AppState>().liveCode;
        if (code.isNotEmpty) _controller.text = code;
      }
      if (mounted) setState(() {});
    });
  }

  @override
  void dispose() {
    _timer.cancel();
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 18, 20, 24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.shield_rounded, color: C.accent),
              const SizedBox(width: 10),
              Expanded(
                child: Text(widget.title,
                    style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
              ),
              IconButton(
                onPressed: () => Navigator.pop(context),
                icon: const Icon(Icons.close_rounded, size: 18, color: C.muted),
              ),
            ],
          ),
          const SizedBox(height: 6),
          const Text(
            'This action needs your second factor. The code below comes from this app — or type it from any authenticator.',
            style: TextStyle(fontSize: 12, color: C.muted, height: 1.4),
          ),
          const SizedBox(height: 16),
          TextField(
            controller: _controller,
            keyboardType: TextInputType.number,
            textAlign: TextAlign.center,
            maxLength: 7,
            style: const TextStyle(fontSize: 26, letterSpacing: 8, fontWeight: FontWeight.w600),
            decoration: const InputDecoration(counterText: ''),
            onChanged: (value) {
              _edited = true;
              setState(() => _error = null);
            },
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: ClipRRect(
                  borderRadius: BorderRadius.circular(4),
                  child: LinearProgressIndicator(
                    value: _seconds / 30,
                    minHeight: 4,
                    backgroundColor: C.elevated,
                  ),
                ),
              ),
              const SizedBox(width: 10),
              Text('${_seconds}s', style: const TextStyle(fontSize: 11, color: C.muted)),
            ],
          ),
          if (_error != null) ...[
            const SizedBox(height: 10),
            Text(_error!, style: const TextStyle(color: C.danger, fontSize: 12)),
          ],
          const SizedBox(height: 18),
          SizedBox(
            width: double.infinity,
            child: FilledButton(
              onPressed: () {
                final code = _controller.text.replaceAll(' ', '');
                if (code.length != 6) {
                  setState(() => _error = 'Enter all six digits.');
                  return;
                }
                Navigator.pop(context, code);
              },
              child: Text(widget.actionLabel),
            ),
          ),
        ],
      ),
    );
  }
}

String formatRelative(int milliseconds) {
  if (milliseconds <= 0) return 'never';
  final diff = DateTime.now().difference(DateTime.fromMillisecondsSinceEpoch(milliseconds));
  if (diff.inSeconds < 20) return 'just now';
  if (diff.inMinutes < 60) return '${diff.inMinutes} min ago';
  if (diff.inHours < 24) return '${diff.inHours}h ago';
  return '${diff.inDays}d ago';
}

String formatClock(int milliseconds) {
  if (milliseconds <= 0) return '—';
  final time = DateTime.fromMillisecondsSinceEpoch(milliseconds);
  final hh = time.hour.toString().padLeft(2, '0');
  final mm = time.minute.toString().padLeft(2, '0');
  final ss = time.second.toString().padLeft(2, '0');
  return '$hh:$mm:$ss';
}

class SectionHeader extends StatelessWidget {
  final String title;
  final Widget? trailing;

  const SectionHeader(this.title, {super.key, this.trailing});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(2, 22, 2, 10),
      child: Row(
        children: [
          Text(title, style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w600)),
          const Spacer(),
          if (trailing != null) trailing!,
        ],
      ),
    );
  }
}

class EventTile extends StatelessWidget {
  final ActivityEvent event;
  final bool showDetail;

  const EventTile({super.key, required this.event, this.showDetail = true});

  @override
  Widget build(BuildContext context) {
    final color = categoryColor(event.category);
    return Container(
      padding: const EdgeInsets.symmetric(vertical: 9),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Container(
            width: 30,
            height: 30,
            decoration: BoxDecoration(
              color: color.withValues(alpha: 0.12),
              borderRadius: BorderRadius.circular(9),
            ),
            child: Icon(categoryIcon(event.category), size: 15, color: color),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(event.title,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(fontSize: 13, height: 1.3)),
                if (showDetail && event.detailText.isNotEmpty) ...[
                  const SizedBox(height: 3),
                  Text(
                    event.detailText,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(fontSize: 11, color: C.muted, height: 1.35),
                  ),
                ],
              ],
            ),
          ),
          const SizedBox(width: 8),
          Column(
            crossAxisAlignment: CrossAxisAlignment.end,
            children: [
              Text(formatClock(event.ts),
                  style: const TextStyle(fontSize: 10, color: C.muted)),
              const SizedBox(height: 3),
              Text(
                event.category.toUpperCase(),
                style: TextStyle(fontSize: 9, color: color.withValues(alpha: 0.9), letterSpacing: 0.4),
              ),
            ],
          ),
        ],
      ),
    );
  }
}
