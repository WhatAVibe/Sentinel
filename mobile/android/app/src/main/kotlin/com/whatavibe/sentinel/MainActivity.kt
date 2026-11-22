package com.whatavibe.sentinel

import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.os.Build
import io.flutter.embedding.android.FlutterFragmentActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

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
    }
}
