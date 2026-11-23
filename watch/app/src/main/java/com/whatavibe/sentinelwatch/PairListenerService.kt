package com.whatavibe.sentinelwatch

import com.google.android.gms.wearable.MessageEvent
import com.google.android.gms.wearable.WearableListenerService
import kotlinx.coroutines.flow.MutableStateFlow
import org.json.JSONObject

object PairEvents {
    val paired = MutableStateFlow<Session?>(null)
}

class PairListenerService : WearableListenerService() {
    companion object {
        const val PATH = "/sentinel/pair"
    }

    override fun onMessageReceived(event: MessageEvent) {
        if (event.path != PATH || event.data.isEmpty()) return
        Thread {
            try {
                val session = parse(event.data.toString(Charsets.UTF_8))
                if (session != null) {
                    Store(applicationContext).save(session)
                    PairEvents.paired.value = session
                }
            } catch (_: Exception) {
            }
        }.start()
    }

    private fun parse(raw: String): Session? {
        val json = JSONObject(raw)
        val url = json.optString("url").trim().trimEnd('/')
        val token = json.optString("token")
        val deviceId = json.optString("device_id")
        if (url.isEmpty() || token.isEmpty() || deviceId.isEmpty()) return null
        return Session(
            baseUrl = url,
            token = token,
            secret = json.optString("secret", ""),
            deviceId = deviceId,
            deviceName = json.optString("device_name", "PC").ifEmpty { "PC" },
            dataKey = json.optString("data_key", ""),
            name = json.optString("name", ""),
        )
    }
}
