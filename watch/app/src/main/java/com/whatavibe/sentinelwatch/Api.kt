package com.whatavibe.sentinelwatch

import org.json.JSONObject
import java.io.BufferedReader
import java.net.HttpURLConnection
import java.net.URL

val DEVICE_NAME: String = run {
    val maker = android.os.Build.MANUFACTURER.replaceFirstChar { it.uppercase() }
    "$maker ${android.os.Build.MODEL}".trim()
}

class ApiException(val status: Int, val code: String) : Exception(code)

data class PairResult(
    val token: String,
    val secret: String,
    val deviceId: String,
    val deviceName: String,
)

data class DeviceStatus(
    val name: String,
    val online: Boolean,
    val away: Boolean,
    val secureLock: Boolean,
    val locked: Boolean,
)

class Api(private val baseUrl: String, private val token: String? = null, private val timeoutMs: Int = 12000) {

    private fun request(method: String, path: String, body: JSONObject? = null): JSONObject {
        val url = URL(baseUrl.trimEnd('/') + path)
        val connection = url.openConnection() as HttpURLConnection
        connection.requestMethod = method
        connection.connectTimeout = timeoutMs
        connection.readTimeout = timeoutMs + 4000
        connection.setRequestProperty("Content-Type", "application/json")
        connection.setRequestProperty("User-Agent", "SentinelWatch/1.3.2")
        connection.setRequestProperty("X-Device-Name", DEVICE_NAME)
        connection.setRequestProperty("X-App-Version", "1.3.2")
        if (!token.isNullOrEmpty()) {
            connection.setRequestProperty("Authorization", "Bearer $token")
        }
        if (body != null) {
            connection.doOutput = true
            connection.outputStream.use { it.write(body.toString().toByteArray()) }
        }
        val status = connection.responseCode
        val stream = if (status in 200..299) connection.inputStream else connection.errorStream
        val text = stream?.bufferedReader()?.use(BufferedReader::readText) ?: ""
        if (status !in 200..299) {
            val error = try {
                JSONObject(text).optString("error", "http_$status")
            } catch (_: Exception) {
                "http_$status"
            }
            throw ApiException(status, error)
        }
        return if (text.isBlank()) JSONObject() else JSONObject(text)
    }

    fun lookup(code: String): String =
        request("POST", "/api/v1/pair/lookup", JSONObject().put("pair_code", code))
            .getString("request_id")

    fun approve(requestId: String, code: String, name: String, uid: String): PairResult {
        val body = JSONObject()
            .put("request_id", requestId)
            .put("pair_code", code)
            .put("mobile_name", name)
            .put("mobile_uid", uid)
        val result = request("POST", "/api/v1/pair/approve", body)
        val device = result.getJSONObject("device")
        return PairResult(
            token = result.getString("mobile_token"),
            secret = result.optString("totp_secret", ""),
            deviceId = device.getString("id"),
            deviceName = device.optString("name", "PC"),
        )
    }

    fun status(deviceId: String): DeviceStatus {
        val device = request("GET", "/api/v1/devices/$deviceId").getJSONObject("device")
        val state = device.optJSONObject("state") ?: JSONObject()
        return DeviceStatus(
            name = device.optString("name", "PC"),
            online = device.optBoolean("online", false),
            away = device.optBoolean("away", false),
            secureLock = state.optBoolean("secure_lock", false),
            locked = device.optBoolean("locked", false),
        )
    }

    fun command(deviceId: String, type: String, args: JSONObject? = null, code: String? = null) {
        val body = JSONObject().put("type", type)
        if (args != null) body.put("args", args)
        if (!code.isNullOrEmpty()) body.put("code", code)
        request("POST", "/api/v1/devices/$deviceId/command", body)
    }
}

fun friendlyError(error: Throwable): String = when (error) {
    is ApiException -> when (error.code) {
        "bad_code" -> "Code rejected"
        "too_many_attempts" -> "Too many attempts"
        "invalid_mobile_token", "unauthorized" -> "Re-pair this watch"
        "phone_not_present" -> "Phone presence required"
        "cannot_remove_current" -> "Cannot remove this phone"
        else -> error.code
    }
    else -> error.message ?: "Network error"
}
