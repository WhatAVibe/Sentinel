import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/app_state.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';

class ActivityPage extends StatefulWidget {
  const ActivityPage({super.key});

  @override
  State<ActivityPage> createState() => _ActivityPageState();
}

class _ActivityPageState extends State<ActivityPage> {
  final TextEditingController _search = TextEditingController();
  String _filter = 'all';
  bool _searching = false;
  bool _grouped = false;
  bool _newestFirst = true;

  static const _filters = [
    ('all', 'All'),
    ('window', 'Windows'),
    ('process', 'Apps'),
    ('network', 'Network'),
    ('search', 'Searches'),
    ('input', 'Input'),
    ('message', 'Messages'),
    ('clipboard', 'Clipboard'),
    ('usb', 'USB'),
    ('session', 'Session'),
    ('security', 'Security'),
    ('power', 'Power'),
  ];

  static const _labels = {
    'all': 'All activity',
    'window': 'Window changes',
    'process': 'Applications',
    'network': 'Network access',
    'search': 'Internet searches',
    'input': 'Mouse & keyboard',
    'message': 'Messages',
    'clipboard': 'Clipboard',
    'usb': 'USB devices',
    'session': 'Sessions',
    'security': 'Security',
    'idle': 'Idle',
    'screenshot': 'Screenshots',
    'system': 'System',
  };

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  bool _matches(ActivityEvent event) {
    switch (_filter) {
      case 'all':
        return true;
      case 'session':
        return event.category == 'session' || event.category == 'idle';
      case 'security':
        return event.category == 'security' || event.category == 'screenshot';
      case 'input':
        return event.category == 'input';
      default:
        return event.category == _filter;
    }
  }

  List<ActivityEvent> _filtered(AppState state) {
    final query = _search.text.trim().toLowerCase();
    final list = state.events.where((event) {
      if (!_matches(event)) return false;
      if (query.isEmpty) return true;
      return event.title.toLowerCase().contains(query) ||
          event.detailText.toLowerCase().contains(query);
    }).toList();
    return _newestFirst ? list : list.reversed.toList();
  }

  int _countFor(AppState state, String key) {
    if (key == 'all') return state.events.length;
    return state.events.where((event) {
      switch (key) {
        case 'session':
          return event.category == 'session' || event.category == 'idle';
        case 'security':
          return event.category == 'security' || event.category == 'screenshot';
        default:
          return event.category == key;
      }
    }).length;
  }

  void _showEvent(BuildContext context, ActivityEvent event) {
    final details = event.detail ?? const <String, dynamic>{};
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      backgroundColor: C.surface,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(22)),
      ),
      builder: (sheetContext) => DraggableScrollableSheet(
        expand: false,
        initialChildSize: 0.62,
        minChildSize: 0.35,
        maxChildSize: 0.94,
        builder: (innerContext, controller) => ListView(
          controller: controller,
          padding: const EdgeInsets.fromLTRB(20, 14, 20, 28),
          children: [
            Center(
              child: Container(
                width: 38,
                height: 4,
                decoration: BoxDecoration(
                  color: C.border,
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 16),
            Row(
              children: [
                Icon(categoryIcon(event.category), size: 18, color: categoryColor(event.category)),
                const SizedBox(width: 8),
                Text(event.category.toUpperCase(),
                    style: TextStyle(
                        fontSize: 10.5,
                        letterSpacing: 0.6,
                        color: categoryColor(event.category),
                        fontWeight: FontWeight.w700)),
                const Spacer(),
                Text('${formatClock(event.ts)} · ${event.severity}',
                    style: const TextStyle(fontSize: 11, color: C.muted)),
              ],
            ),
            const SizedBox(height: 10),
            SelectableText(event.title,
                style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w600, height: 1.3)),
            const SizedBox(height: 6),
            Text(_dayLabel(event.time),
                style: const TextStyle(fontSize: 11, color: C.muted)),
            const SizedBox(height: 18),
            if (details.isEmpty)
              const Text('No extra details for this event.',
                  style: TextStyle(fontSize: 12, color: C.muted))
            else
              for (final entry in details.entries)
                Padding(
                  padding: const EdgeInsets.only(bottom: 12),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(entry.key.replaceAll('_', ' ').toUpperCase(),
                          style: const TextStyle(
                              fontSize: 9.5,
                              letterSpacing: 0.7,
                              color: C.muted,
                              fontWeight: FontWeight.w700)),
                      const SizedBox(height: 3),
                      SelectableText(
                        '${entry.value}'.replaceAll('\\n', '\n'),
                        style: const TextStyle(fontSize: 13, height: 1.45),
                      ),
                    ],
                  ),
                ),
          ],
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final events = _filtered(state);

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 8, 16, 0),
          child: Column(
            children: [
              if (_searching)
                TextField(
                  controller: _search,
                  autofocus: true,
                  decoration: InputDecoration(
                    hintText: 'Search the timeline',
                    prefixIcon: const Icon(Icons.search_rounded, size: 18, color: C.muted),
                    suffixIcon: IconButton(
                      icon: const Icon(Icons.close_rounded, size: 16, color: C.muted),
                      onPressed: () => setState(() {
                        _searching = false;
                        _search.clear();
                      }),
                    ),
                  ),
                  onChanged: (_) => setState(() {}),
                )
              else
                Row(
                  children: [
                    Text('${events.length} events',
                        style: const TextStyle(fontSize: 11, color: C.muted)),
                    const Spacer(),
                    if (state.socketLive)
                      const Pill('LIVE', color: C.success)
                    else
                      const Pill('POLLING', color: C.muted),
                    const SizedBox(width: 6),
                    IconButton(
                      tooltip: _newestFirst ? 'Newest first' : 'Oldest first',
                      onPressed: () => setState(() => _newestFirst = !_newestFirst),
                      icon: Icon(
                        _newestFirst ? Icons.arrow_downward_rounded : Icons.arrow_upward_rounded,
                        size: 18,
                        color: C.muted,
                      ),
                    ),
                    IconButton(
                      tooltip: _grouped ? 'Timeline' : 'Group by category',
                      onPressed: () => setState(() => _grouped = !_grouped),
                      icon: Icon(_grouped ? Icons.timeline_rounded : Icons.dashboard_customize_rounded,
                          size: 18, color: _grouped ? C.accent : C.muted),
                    ),
                    IconButton(
                      onPressed: () => setState(() => _searching = true),
                      icon: const Icon(Icons.search_rounded, size: 18, color: C.muted),
                    ),
                  ],
                ),
              SizedBox(
                height: 38,
                child: ListView.separated(
                  scrollDirection: Axis.horizontal,
                  itemCount: _filters.length,
                  separatorBuilder: (_, __) => const SizedBox(width: 8),
                  itemBuilder: (context, index) {
                    final (key, label) = _filters[index];
                    final active = _filter == key;
                    final count = _countFor(state, key);
                    return GestureDetector(
                      onTap: () => setState(() {
                        _filter = key;
                        _grouped = false;
                      }),
                      child: Container(
                        alignment: Alignment.center,
                        padding: const EdgeInsets.symmetric(horizontal: 12),
                        decoration: BoxDecoration(
                          color: active ? C.accentDim : C.elevated,
                          borderRadius: BorderRadius.circular(10),
                          border: Border.all(color: active ? C.accent : C.border),
                        ),
                        child: Text(
                          '$label · $count',
                          style: TextStyle(
                            fontSize: 11.5,
                            fontWeight: FontWeight.w600,
                            color: active ? C.text : C.muted,
                          ),
                        ),
                      ),
                    );
                  },
                ),
              ),
            ],
          ),
        ),
        Expanded(
          child: events.isEmpty
              ? const EmptyState(
                  icon: Icons.timeline_rounded,
                  title: 'Nothing here yet',
                  subtitle: 'Arm away mode or take a screenshot to start the timeline.',
                )
              : _grouped
                  ? _buildGroups(state, events)
                  : RefreshIndicator(
                      onRefresh: state.refreshDevice,
                      color: C.accent,
                      backgroundColor: C.surface,
                      child: ListView.builder(
                        padding: const EdgeInsets.fromLTRB(12, 8, 16, 24),
                        itemCount: events.length + (state.eventsHasMore ? 1 : 0),
                        itemBuilder: (context, index) {
                          final olderAtTop = state.eventsHasMore && !_newestFirst;
                          if (olderAtTop && index == 0) return _olderButton(state);
                          final eventIndex = olderAtTop ? index - 1 : index;
                          if (eventIndex >= events.length) return _olderButton(state);
                          final event = events[eventIndex];
                          final previous = eventIndex == 0 ? null : events[eventIndex - 1];
                          final showHeader =
                              previous == null || !_sameDay(previous.time, event.time);
                          return Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              if (showHeader)
                                Padding(
                                  padding: EdgeInsets.fromLTRB(8, eventIndex == 0 ? 6 : 18, 0, 6),
                                  child: Text(
                                    _dayLabel(event.time),
                                    style: const TextStyle(
                                      fontSize: 11,
                                      fontWeight: FontWeight.w600,
                                      color: C.muted,
                                      letterSpacing: 0.4,
                                    ),
                                  ),
                                ),
                              GestureDetector(
                                onTap: () => _showEvent(context, event),
                                child: Container(
                                  decoration: BoxDecoration(
                                    color: C.surface,
                                    borderRadius: BorderRadius.circular(12),
                                    border: Border.all(color: C.border),
                                  ),
                                  padding: const EdgeInsets.symmetric(horizontal: 12),
                                  child: EventTile(event: event),
                                ),
                              ),
                              const SizedBox(height: 6),
                            ],
                          );
                        },
                      ),
                    ),
        ),
      ],
    );
  }

  Widget _olderButton(AppState state) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(8, 12, 8, 20),
      child: OutlinedButton.icon(
        onPressed: state.eventsLoadingOlder ? null : state.loadOlderEvents,
        icon: state.eventsLoadingOlder
            ? const SizedBox(
                width: 14,
                height: 14,
                child: CircularProgressIndicator(strokeWidth: 2),
              )
            : const Icon(Icons.history_rounded, size: 16),
        label: Text(state.eventsLoadingOlder ? 'Loading older events…' : 'Load older events'),
      ),
    );
  }

  Widget _buildGroups(AppState state, List<ActivityEvent> events) {
    final groups = <String, List<ActivityEvent>>{};
    for (final event in events) {
      groups.putIfAbsent(event.category, () => []).add(event);
    }
    final ordered = groups.entries.toList()
      ..sort((a, b) => b.value.length.compareTo(a.value.length));

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 10, 16, 24),
      children: [
        for (final group in ordered)
          Padding(
            padding: const EdgeInsets.only(bottom: 10),
            child: GestureDetector(
              onTap: () => setState(() {
                _filter = group.key;
                _grouped = false;
              }),
              child: CardBox(
                padding: const EdgeInsets.all(14),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Container(
                          width: 34,
                          height: 34,
                          decoration: BoxDecoration(
                            color: categoryColor(group.key).withValues(alpha: 0.12),
                            borderRadius: BorderRadius.circular(10),
                          ),
                          child: Icon(categoryIcon(group.key), size: 17, color: categoryColor(group.key)),
                        ),
                        const SizedBox(width: 12),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(_labels[group.key] ?? group.key,
                                  style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600)),
                              Text(
                                '${group.value.length} events · last ${formatRelative(group.value.first.ts)}',
                                style: const TextStyle(fontSize: 10.5, color: C.muted),
                              ),
                            ],
                          ),
                        ),
                        const Icon(Icons.chevron_right_rounded, size: 18, color: C.muted),
                      ],
                    ),
                    const SizedBox(height: 10),
                    for (final event in group.value.take(3))
                      Padding(
                        padding: const EdgeInsets.only(bottom: 4),
                        child: Row(
                          children: [
                            Text(formatClock(event.ts),
                                style: const TextStyle(
                                    fontSize: 9.5, color: C.muted, fontFamily: 'monospace')),
                            const SizedBox(width: 8),
                            Expanded(
                              child: Text(event.title,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: const TextStyle(fontSize: 11.5)),
                            ),
                          ],
                        ),
                      ),
                  ],
                ),
              ),
            ),
          ),
      ],
    );
  }

  static bool _sameDay(DateTime a, DateTime b) =>
      a.year == b.year && a.month == b.month && a.day == b.day;

  static String _dayLabel(DateTime time) {
    final now = DateTime.now();
    final today = DateTime(now.year, now.month, now.day);
    final day = DateTime(time.year, time.month, time.day);
    final diff = today.difference(day).inDays;
    if (diff == 0) return 'Today';
    if (diff == 1) return 'Yesterday';
    return '${time.day.toString().padLeft(2, '0')} ${_month(time.month)} ${time.year}';
  }

  static String _month(int month) => const [
        'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
        'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'
      ][month - 1];
}
