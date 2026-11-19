package com.pcmonitor.sentinel

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
    }
}
