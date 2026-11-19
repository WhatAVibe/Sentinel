import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/app_state.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets.dart';

class GalleryPage extends StatelessWidget {
  const GalleryPage({super.key});

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    final shots = state.shots;

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 10, 16, 8),
          child: Row(
            children: [
              Pill('${shots.length} captures', color: C.muted),
              const Spacer(),
              IconButton(
                onPressed: state.loadShots,
                icon: const Icon(Icons.refresh_rounded, size: 18, color: C.muted),
              ),
              const SizedBox(width: 4),
              FilledButton.icon(
                style: FilledButton.styleFrom(
                  padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                ),
                onPressed: state.busy
                    ? null
                    : () async {
                        final result = await state.command('screenshot');
                        if (!context.mounted) return;
                        ScaffoldMessenger.of(context).showSnackBar(
                          SnackBar(
                            content: Text(result.ok
                                ? 'Capturing every display…'
                                : friendlyError(result.error)),
                          ),
                        );
                      },
                icon: const Icon(Icons.photo_camera_rounded, size: 16),
                label: const Text('Capture'),
              ),
            ],
          ),
        ),
        Expanded(
          child: shots.isEmpty
              ? const EmptyState(
                  icon: Icons.photo_library_outlined,
                  title: 'No screenshots yet',
                  subtitle: 'Tap Capture, or take one from the PC tray.',
                )
              : RefreshIndicator(
                  onRefresh: state.loadShots,
                  color: C.accent,
                  backgroundColor: C.surface,
                  child: GridView.builder(
                    padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
                    gridDelegate: const SliverGridDelegateWithFixedCrossAxisCount(
                      crossAxisCount: 2,
                      mainAxisSpacing: 10,
                      crossAxisSpacing: 10,
                      childAspectRatio: 0.82,
                    ),
                    itemCount: shots.length + (state.shotsHasMore ? 1 : 0),
                    itemBuilder: (context, index) {
                      if (index >= shots.length) {
                        return Container(
                          decoration: BoxDecoration(
                            color: C.surface,
                            borderRadius: BorderRadius.circular(14),
                            border: Border.all(color: C.border),
                          ),
                          alignment: Alignment.center,
                          child: TextButton.icon(
                            onPressed: state.shotsLoadingOlder ? null : state.loadOlderShots,
                            icon: state.shotsLoadingOlder
                                ? const SizedBox(
                                    width: 14,
                                    height: 14,
                                    child: CircularProgressIndicator(strokeWidth: 2),
                                  )
                                : const Icon(Icons.history_rounded, size: 16),
                            label: Text(state.shotsLoadingOlder ? 'Loading…' : 'Load older'),
                          ),
                        );
                      }
                      final shot = shots[index];
                      return _ShotCard(shot: shot, state: state);
                    },
                  ),
                ),
        ),
      ],
    );
  }
}

class _ShotCard extends StatelessWidget {
  final Shot shot;
  final AppState state;

  const _ShotCard({required this.shot, required this.state});

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: () => Navigator.of(context).push(
        MaterialPageRoute(builder: (_) => ShotViewer(shot: shot)),
      ),
      child: Container(
        decoration: BoxDecoration(
          color: C.surface,
          borderRadius: BorderRadius.circular(14),
          border: Border.all(color: C.border),
        ),
        clipBehavior: Clip.antiAlias,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: Image.network(
                state.imageUrl(shot),
                headers: state.imageHeaders,
                fit: BoxFit.cover,
                width: double.infinity,
                errorBuilder: (_, __, ___) => Container(
                  color: C.elevated,
                  alignment: Alignment.center,
                  child: const Icon(Icons.broken_image_outlined, color: C.muted, size: 22),
                ),
                loadingBuilder: (context, child, progress) {
                  if (progress == null) return child;
                  return Container(
                    color: C.elevated,
                    alignment: Alignment.center,
                    child: const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    ),
                  );
                },
              ),
            ),
            Padding(
              padding: const EdgeInsets.all(10),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    shot.caption,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(fontSize: 11, fontWeight: FontWeight.w600),
                  ),
                  const SizedBox(height: 3),
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          formatRelative(shot.ts),
                          style: const TextStyle(fontSize: 10, color: C.muted),
                        ),
                      ),
                      if (shot.width > 0)
                        Text(
                          '${shot.width}×${shot.height}',
                          style: const TextStyle(fontSize: 9, color: C.muted),
                        ),
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class ShotViewer extends StatelessWidget {
  final Shot shot;

  const ShotViewer({super.key, required this.shot});

  @override
  Widget build(BuildContext context) {
    final state = context.watch<AppState>();
    return Scaffold(
      backgroundColor: Colors.black,
      appBar: AppBar(
        backgroundColor: Colors.black,
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(shot.caption, style: const TextStyle(fontSize: 14)),
            Text(
              '${shot.time.toString().substring(0, 16)} · ${shot.trigger}',
              style: const TextStyle(fontSize: 10, color: C.muted),
            ),
          ],
        ),
      ),
      body: Center(
        child: InteractiveViewer(
          minScale: 0.8,
          maxScale: 6,
          child: Image.network(
            state.imageUrl(shot),
            headers: state.imageHeaders,
            fit: BoxFit.contain,
            errorBuilder: (_, __, ___) => const Icon(
              Icons.broken_image_outlined,
              color: C.muted,
              size: 40,
            ),
            loadingBuilder: (context, child, progress) {
              if (progress == null) return child;
              return const Center(child: CircularProgressIndicator());
            },
          ),
        ),
      ),
    );
  }
}
