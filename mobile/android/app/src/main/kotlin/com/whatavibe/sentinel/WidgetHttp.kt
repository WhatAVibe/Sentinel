package com.whatavibe.sentinel

import android.content.Context
import android.widget.Toast
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

object WidgetTotp {
    private const val ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
    private const val STEP = 30

    fun now(secret: String): String {
        if (secret.isBlank()) return "------"
        val key = base32(secret)
        if (key.isEmpty()) return "------"
        var counter = System.currentTimeMillis() / 1000 / STEP
        val message = ByteArray(8)
        for (index in 7 downTo 0) {
            message[index] = (counter and 0xFF).toByte()
            counter = counter shr 8
        }
        val mac = Mac.getInstance("HmacSHA1")
        mac.init(SecretKeySpec(key, "HmacSHA1"))
        val hash = mac.doFinal(message)
        val offset = hash[hash.size - 1].toInt() and 0x0F
        val value = ((hash[offset].toInt() and 0x7F) shl 24) or
            ((hash[offset + 1].toInt() and 0xFF) shl 16) or
            ((hash[offset + 2].toInt() and 0xFF) shl 8) or
            (hash[offset + 3].toInt() and 0xFF)
        return (value % 1_000_000).toString().padStart(6, '0')
    }

    fun secondsRemaining(): Int = STEP - ((System.currentTimeMillis() / 1000) % STEP).toInt()

    private fun base32(input: String): ByteArray {
        val clean = input.uppercase().filter { it in ALPHABET }
        val output = ArrayList<Byte>(clean.length * 5 / 8)
        var buffer = 0
        var bits = 0
        for (character in clean) {
            buffer = (buffer shl 5) or ALPHABET.indexOf(character)
            bits += 5
            if (bits >= 8) {
                bits -= 8
                output.add(((buffer shr bits) and 0xFF).toByte())
            }
        }
        return output.toByteArray()
    }
}

object WidgetHttp {
    fun status(context: Context): Pair<String, Boolean>? {
        val config = WidgetStore.config(context) ?: return null
        return try {
            val connection = URL("${config.baseUrl}/api/v1/me").openConnection() as HttpURLConnection
            connection.requestMethod = "GET"
            connection.connectTimeout = 5000
            connection.readTimeout = 5000
            connection.setRequestProperty("Authorization", "Bearer ${config.token}")
            connection.setRequestProperty("User-Agent", "SentinelWidget")
            if (connection.responseCode !in 200..299) return null
            val body = connection.inputStream.bufferedReader().use { it.readText() }
            val device = JSONObject(body).getJSONObject("device")
            val state = device.optJSONObject("state") ?: JSONObject()
            val label = when {
                !device.optBoolean("online") -> "Offline"
                state.optBoolean("secure_lock") -> "Secure lock"
                device.optBoolean("away") -> "Away mode"
                else -> "Online"
            }
            label to device.optBoolean("online")
        } catch (_: Exception) {
            null
        }
    }

    fun command(context: Context, type: String): Boolean {
        val config = WidgetStore.config(context) ?: return false
        return try {
            val connection = URL("${config.baseUrl}/api/v1/devices/${config.deviceId}/command")
                .openConnection() as HttpURLConnection
            connection.requestMethod = "POST"
            connection.doOutput = true
            connection.connectTimeout = 6000
            connection.readTimeout = 6000
            connection.setRequestProperty("Authorization", "Bearer ${config.token}")
            connection.setRequestProperty("Content-Type", "application/json")
            connection.setRequestProperty("User-Agent", "SentinelWidget")
            val body = JSONObject().put("type", type).toString()
            connection.outputStream.use { it.write(body.toByteArray()) }
            connection.responseCode in 200..299
        } catch (_: Exception) {
            false
        }
    }

    fun notify(context: Context, text: String) {
        android.os.Handler(context.mainLooper).post {
            Toast.makeText(context, text, Toast.LENGTH_SHORT).show()
        }
    }
}
