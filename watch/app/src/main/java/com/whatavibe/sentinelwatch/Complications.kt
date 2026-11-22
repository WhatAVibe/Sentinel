package com.whatavibe.sentinelwatch

import android.app.PendingIntent
import android.content.Intent
import androidx.wear.watchface.complications.data.ComplicationData
import androidx.wear.watchface.complications.data.ComplicationType
import androidx.wear.watchface.complications.data.LongTextComplicationData
import androidx.wear.watchface.complications.data.PlainComplicationText
import androidx.wear.watchface.complications.data.RangedValueComplicationData
import androidx.wear.watchface.complications.data.ShortTextComplicationData
import androidx.wear.watchface.complications.datasource.ComplicationRequest
import androidx.wear.watchface.complications.datasource.SuspendingComplicationDataSourceService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

class SentinelComplicationService : SuspendingComplicationDataSourceService() {

    override suspend fun onComplicationRequest(request: ComplicationRequest): ComplicationData? {
        val snapshot = withContext(Dispatchers.IO) { loadSnapshot(this@SentinelComplicationService) }
        return data(request.complicationType, snapshot)
    }

    override fun getPreviewData(type: ComplicationType): ComplicationData? =
        data(type, TileSnapshot(true, "VIBE", true, false, false, "123456", 18))

    private fun statusLine(snapshot: TileSnapshot): String = when {
        !snapshot.paired -> "Pair in the app"
        !snapshot.online -> "Offline"
        snapshot.secureLock -> "Secure lock"
        snapshot.away -> "Away"
        else -> "Online"
    }

    private fun code(snapshot: TileSnapshot): String =
        if (snapshot.paired) snapshot.code.chunked(3).joinToString(" ") else "------"

    private fun data(type: ComplicationType, snapshot: TileSnapshot): ComplicationData? {
        val status = PlainComplicationText.Builder(statusLine(snapshot)).build()
        val code = PlainComplicationText.Builder(code(snapshot)).build()
        val label = PlainComplicationText.Builder("Sentinel").build()
        return when (type) {
            ComplicationType.RANGED_VALUE -> RangedValueComplicationData.Builder(
                snapshot.seconds.coerceIn(0, 30).toFloat(),
                0f,
                30f,
                status,
            )
                .setText(code)
                .setTapAction(openApp())
                .build()
            ComplicationType.SHORT_TEXT -> ShortTextComplicationData.Builder(status, code)
                .setTapAction(openApp())
                .build()
            ComplicationType.LONG_TEXT -> LongTextComplicationData.Builder(
                PlainComplicationText.Builder("${statusLine(snapshot)} · ${code(snapshot)}").build(),
                label,
            )
                .setTapAction(openApp())
                .build()
            else -> null
        }
    }

    private fun openApp(): PendingIntent {
        val intent = Intent(this, MainActivity::class.java)
            .putExtra(MainActivity.EXTRA_SCREEN, MainActivity.SCREEN_CODE)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        return PendingIntent.getActivity(
            this,
            0,
            intent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
    }
}
