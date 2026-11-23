package com.whatavibe.sentinel

import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.os.Build
import com.google.android.gms.tasks.Tasks
import com.google.android.gms.wearable.Wearable
import io.flutter.embedding.android.FlutterFragmentActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import org.json.JSONObject
import java.util.concurrent.TimeUnit

private const val WATCH_PAIR_PATH = "/sentinel/pair"

class MainActivity : FlutterFragmentActivity() {
    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "sentinel/device")
            .setMethodCallHandler { call, result ->
                if (call.method == "name") {
                    val maker = Build.MANUFACTURER.replaceFirstChar { it.uppercase() }
                    result.success("$maker ${Build.MODEL}".trim())
                } else {
                    result.notImplemented()
                }
            }
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "sentinel/widgets")
            .setMethodCallHandler { call, result ->
                when (call.method) {
                    "configure" -> {
                        val args = call.arguments as? Map<*, *>
                        if (args == null) {
                            result.error("bad_args", "missing configuration", null)
                            return@setMethodCallHandler
                        }
                        WidgetStore.save(
                            this,
                            WidgetConfig(
                                baseUrl = args["baseUrl"] as? String ?: "",
                                token = args["token"] as? String ?: "",
                                deviceId = args["deviceId"] as? String ?: "",
                                name = args["name"] as? String ?: "Sentinel",
                                totp = args["totp"] as? String ?: "",
                                allowWithoutUnlock = args["allowWithoutUnlock"] as? Boolean ?: false,
                                showCode = args["showCode"] as? Boolean ?: true,
                            ),
                        )
                        WidgetRefresher.refresh(this)
                        result.success(true)
                    }
                    "clear" -> {
                        WidgetStore.clear(this)
                        WidgetRefresher.refresh(this)
                        result.success(true)
                    }
                    "refresh" -> {
                        WidgetRefresher.refresh(this)
                        result.success(true)
                    }
                    "pin" -> {
                        val cls = when (call.argument<String>("kind")) {
                            "code" -> CodeWidgetProvider::class.java
                            "actions" -> ActionsWidgetProvider::class.java
                            else -> StatusWidgetProvider::class.java
                        }
                        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
                            result.success(false)
                            return@setMethodCallHandler
                        }
                        val manager = AppWidgetManager.getInstance(this)
                        result.success(manager.requestPinAppWidget(ComponentName(this, cls), null, null))
                    }
                    else -> result.notImplemented()
                }
            }
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "sentinel/watch")
            .setMethodCallHandler { call, result ->
                if (call.method != "pair") {
                    result.notImplemented()
                    return@setMethodCallHandler
                }
                val args = call.arguments as? Map<*, *>
                val token = (args?.get("token") as? String).orEmpty()
                val dataKey = (args?.get("dataKey") as? String).orEmpty()
                if (args == null || token.isEmpty() || dataKey.isEmpty()) {
                    result.error("bad_args", "token and dataKey are required", null)
                    return@setMethodCallHandler
                }
                pairWatch(args) { accepted -> result.success(accepted) }
            }
    }

    private fun pairWatch(args: Map<*, *>, done: (Boolean) -> Unit) {
        Thread {
            var accepted = false
            try {
                val payload = JSONObject()
                    .put("url", args["url"] as? String ?: "")
                    .put("device_id", args["deviceId"] as? String ?: "")
                    .put("device_name", args["deviceName"] as? String ?: "")
                    .put("token", args["token"] as? String ?: "")
                    .put("secret", args["secret"] as? String ?: "")
                    .put("data_key", args["dataKey"] as? String ?: "")
                    .put("name", args["name"] as? String ?: "Watch")
                    .toString()
                    .toByteArray(Charsets.UTF_8)
                val nodes = Tasks.await(
                    Wearable.getNodeClient(applicationContext).connectedNodes,
                    10,
                    TimeUnit.SECONDS,
                )
                val messages = Wearable.getMessageClient(applicationContext)
                for (node in nodes) {
                    try {
                        Tasks.await(
                            messages.sendMessage(node.id, WATCH_PAIR_PATH, payload),
                            10,
                            TimeUnit.SECONDS,
                        )
                        accepted = true
                    } catch (_: Exception) {
                        // Node dropped; keep trying the rest.
                    }
                }
            } catch (_: Exception) {
                // No Wear OS app, Bluetooth off, or Play services unavailable.
            }
            runOnUiThread { done(accepted) }
        }.start()
    }
}
