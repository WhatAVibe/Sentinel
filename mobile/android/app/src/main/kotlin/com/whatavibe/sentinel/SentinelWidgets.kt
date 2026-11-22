package com.whatavibe.sentinel

import android.app.AlarmManager
import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.widget.RemoteViews
import kotlin.concurrent.thread

private const val ACTION_TICK = "com.whatavibe.sentinel.WIDGET_TICK"

object WidgetRefresher {
    fun refresh(context: Context) {
        thread(name = "sentinel-widget") {
            val config = WidgetStore.config(context)
            val status = if (config != null) WidgetHttp.status(context) else null
            val manager = AppWidgetManager.getInstance(context)
            val state = status?.first ?: if (config == null) "Pair Sentinel" else "Offline"
            val code = if (config != null && config.showCode) WidgetTotp.now(config.totp) else "------"
            val seconds = WidgetTotp.secondsRemaining()
            val name = config?.name ?: "Sentinel"

            for (id in manager.getAppWidgetIds(ComponentName(context, StatusWidgetProvider::class.java))) {
                manager.updateAppWidget(id, statusViews(context, name, state, code, seconds))
            }
            for (id in manager.getAppWidgetIds(ComponentName(context, CodeWidgetProvider::class.java))) {
                manager.updateAppWidget(id, codeViews(context, code, seconds))
            }
            for (id in manager.getAppWidgetIds(ComponentName(context, ActionsWidgetProvider::class.java))) {
                manager.updateAppWidget(id, actionViews(context))
            }
            schedule(context)
        }
    }

    fun schedule(context: Context) {
        val intent = Intent(context, StatusWidgetProvider::class.java).setAction(ACTION_TICK)
        val pending = PendingIntent.getBroadcast(
            context,
            10,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val alarm = context.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        try {
            alarm.setAndAllowWhileIdle(AlarmManager.RTC, System.currentTimeMillis() + 30_000, pending)
        } catch (_: Exception) {
            // widgets still refresh when opened or when the app runs
        }
    }

    private fun openApp(context: Context): PendingIntent {
        val intent = Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        return PendingIntent.getActivity(
            context,
            0,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }

    private fun tick(context: Context): PendingIntent {
        val intent = Intent(context, StatusWidgetProvider::class.java).setAction(ACTION_TICK)
        return PendingIntent.getBroadcast(
            context,
            11,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }

    private fun action(context: Context, type: String): PendingIntent {
        val intent = Intent(context, WidgetActionReceiver::class.java)
            .setAction("com.whatavibe.sentinel.WIDGET_ACTION")
            .putExtra("type", type)
        return PendingIntent.getBroadcast(
            context,
            type.hashCode(),
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }

    fun statusViews(
        context: Context,
        name: String,
        state: String,
        code: String,
        seconds: Int,
    ): RemoteViews = RemoteViews(context.packageName, R.layout.sentinel_widget_status).apply {
        setTextViewText(R.id.widget_name, name)
        setTextViewText(R.id.widget_state, state)
        setTextViewText(R.id.widget_code, code.chunked(3).joinToString(" "))
        setTextViewText(R.id.widget_timer, "$seconds s")
        setOnClickPendingIntent(R.id.widget_root, openApp(context))
        setOnClickPendingIntent(R.id.widget_refresh, tick(context))
    }

    private fun codeViews(context: Context, code: String, seconds: Int): RemoteViews =
        RemoteViews(context.packageName, R.layout.sentinel_widget_code).apply {
            setTextViewText(R.id.code_value, code.chunked(3).joinToString(" "))
            setTextViewText(R.id.code_timer, "$seconds s")
            setOnClickPendingIntent(R.id.code_root, openApp(context))
            setOnClickPendingIntent(R.id.code_refresh, tick(context))
        }

    private fun actionViews(context: Context): RemoteViews =
        RemoteViews(context.packageName, R.layout.sentinel_widget_actions).apply {
            setOnClickPendingIntent(R.id.action_lock, action(context, "lock"))
            setOnClickPendingIntent(R.id.action_secure, action(context, "lock_screen"))
            setOnClickPendingIntent(R.id.action_away, action(context, "away_enable"))
            setOnClickPendingIntent(R.id.action_shot, action(context, "screenshot"))
            setOnClickPendingIntent(R.id.action_open, openApp(context))
        }
}

class StatusWidgetProvider : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        WidgetRefresher.refresh(context)
    }

    override fun onReceive(context: Context, intent: Intent) {
        super.onReceive(context, intent)
        if (intent.action == ACTION_TICK || intent.action == AppWidgetManager.ACTION_APPWIDGET_UPDATE) {
            WidgetRefresher.refresh(context)
        }
    }
}

class CodeWidgetProvider : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        WidgetRefresher.refresh(context)
    }
}

class ActionsWidgetProvider : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        WidgetRefresher.refresh(context)
    }
}

class WidgetActionReceiver : android.content.BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val type = intent.getStringExtra("type") ?: return
        val config = WidgetStore.config(context) ?: return
        if (!config.allowWithoutUnlock) {
            val open = Intent(context, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            context.startActivity(open)
            WidgetHttp.notify(context, "Open Sentinel to run this action")
            return
        }
        val pending = goAsync()
        thread(name = "sentinel-widget-action") {
            try {
                val ok = WidgetHttp.command(context, type)
                WidgetHttp.notify(
                    context,
                    when {
                        !ok -> "Sentinel: action failed"
                        type == "lock" -> "PC locked"
                        type == "lock_screen" -> "Secure lock engaged"
                        type == "away_enable" -> "Away mode enabled"
                        type == "screenshot" -> "Screenshot requested"
                        else -> "Sent"
                    },
                )
            } finally {
                pending.finish()
                WidgetRefresher.refresh(context)
            }
        }
    }
}
