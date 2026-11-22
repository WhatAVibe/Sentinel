package com.whatavibe.sentinel

import android.app.PendingIntent
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import kotlin.concurrent.thread

class SentinelTileService : TileService() {
    override fun onStartListening() {
        super.onStartListening()
        val config = WidgetStore.config(this)
        qsTile?.apply {
            state = Tile.STATE_INACTIVE
            label = if (config == null) "Pair Sentinel" else "Lock ${config.name}"
            subtitle = config?.let { "Tap to secure lock" }
            updateTile()
        }
        if (config != null) {
            thread(name = "sentinel-tile-status") {
                val status = WidgetHttp.status(this)?.first ?: "Offline"
                Handler(Looper.getMainLooper()).post {
                    qsTile?.apply {
                        subtitle = status
                        updateTile()
                    }
                }
            }
        }
    }

    override fun onClick() {
        super.onClick()
        val config = WidgetStore.config(this)
        if (config == null || !config.allowWithoutUnlock) {
            openApp()
            return
        }
        qsTile?.apply {
            state = Tile.STATE_ACTIVE
            updateTile()
        }
        thread(name = "sentinel-tile") {
            val ok = WidgetHttp.command(this, "lock_screen")
            WidgetHttp.notify(this, if (ok) "Secure lock engaged" else "Sentinel: action failed")
            Handler(Looper.getMainLooper()).post {
                qsTile?.apply {
                    state = Tile.STATE_INACTIVE
                    updateTile()
                }
            }
        }
    }

    private fun openApp() {
        val intent = Intent(this, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        val pending = PendingIntent.getActivity(
            this,
            0,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        startActivityAndCollapse(pending)
    }
}
