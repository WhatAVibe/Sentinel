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

class SentinelComplicationService : SuspendingComplicationDataSourceService() {

    override suspend fun onComplicationRequest(request: ComplicationRequest): ComplicationData? =
        data(request.complicationType)

    override fun getPreviewData(type: ComplicationType): ComplicationData? = data(type)

    private fun data(type: ComplicationType): ComplicationData? {
        val label = PlainComplicationText.Builder("Sentinel").build()
        val description = PlainComplicationText.Builder("Open Sentinel").build()
        return when (type) {
            ComplicationType.RANGED_VALUE -> RangedValueComplicationData.Builder(
                0f,
                0f,
                1f,
                description,
            )
                .setText(label)
                .setTapAction(openApp())
                .build()
            ComplicationType.SHORT_TEXT -> ShortTextComplicationData.Builder(label, description)
                .setTapAction(openApp())
                .build()
            ComplicationType.LONG_TEXT -> LongTextComplicationData.Builder(label, description)
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
