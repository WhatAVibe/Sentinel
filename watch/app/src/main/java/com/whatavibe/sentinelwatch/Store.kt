package com.whatavibe.sentinelwatch

import android.content.Context
import java.util.UUID

data class Session(
    val baseUrl: String,
    val token: String,
    val secret: String,
    val deviceId: String,
    val deviceName: String,
    val dataKey: String = "",
    val name: String = "",
)

data class TileConfig(
    val name: Boolean = true,
    val status: Boolean = true,
    val code: Boolean = true,
    val seconds: Boolean = true,
    val actions: List<String> = listOf("lock", "unlock", "away", "secure"),
)

class Store(context: Context) {
    private val prefs = context.getSharedPreferences("sentinel_watch", Context.MODE_PRIVATE)

    fun uid(): String {
        prefs.getString("uid", null)?.let { return it }
        val id = UUID.randomUUID().toString()
        prefs.edit().putString("uid", id).apply()
        return id
    }

    fun load(): Session? {
        val url = prefs.getString("server", null) ?: return null
        val token = prefs.getString("token", null) ?: return null
        val device = prefs.getString("device", null) ?: return null
        return Session(
            baseUrl = url,
            token = token,
            secret = prefs.getString("secret", "") ?: "",
            deviceId = device,
            deviceName = prefs.getString("device_name", "PC") ?: "PC",
            dataKey = prefs.getString("data_key", "") ?: "",
            name = prefs.getString("name", "") ?: "",
        )
    }

    fun save(session: Session) {
        prefs.edit()
            .putString("server", session.baseUrl)
            .putString("token", session.token)
            .putString("secret", session.secret)
            .putString("device", session.deviceId)
            .putString("device_name", session.deviceName)
            .putString("data_key", session.dataKey)
            .putString("name", session.name)
            .apply()
    }

    fun clear() = prefs.edit().clear().apply()

    fun tileConfig(): TileConfig = TileConfig(
        name = prefs.getBoolean("tile_name", true),
        status = prefs.getBoolean("tile_status", true),
        code = prefs.getBoolean("tile_code", true),
        seconds = prefs.getBoolean("tile_seconds", true),
        actions = (prefs.getString("tile_actions", "lock,unlock,away,secure") ?: "")
            .split(',')
            .filter { it.isNotEmpty() },
    )

    fun saveTileConfig(config: TileConfig) {
        prefs.edit()
            .putBoolean("tile_name", config.name)
            .putBoolean("tile_status", config.status)
            .putBoolean("tile_code", config.code)
            .putBoolean("tile_seconds", config.seconds)
            .putString("tile_actions", config.actions.joinToString(","))
            .apply()
    }
}
