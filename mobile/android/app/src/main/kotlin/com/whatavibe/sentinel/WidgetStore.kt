package com.whatavibe.sentinel

import android.content.Context
import android.util.Base64
import org.json.JSONObject
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties

data class WidgetConfig(
    val baseUrl: String,
    val token: String,
    val deviceId: String,
    val name: String,
    val totp: String,
    val allowWithoutUnlock: Boolean,
    val showCode: Boolean,
)

object WidgetStore {
    private const val PREFS = "sentinel_widgets"
    private const val KEY = "config"
    private const val ALIAS = "sentinel_widget_key"

    fun save(context: Context, config: WidgetConfig) {
        val json = JSONObject()
            .put("baseUrl", config.baseUrl)
            .put("token", config.token)
            .put("deviceId", config.deviceId)
            .put("name", config.name)
            .put("totp", config.totp)
            .put("allowWithoutUnlock", config.allowWithoutUnlock)
            .put("showCode", config.showCode)
            .toString()
        val encrypted = encrypt(json)
        if (encrypted != null) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().putString(KEY, encrypted).apply()
        }
    }

    fun config(context: Context): WidgetConfig? {
        val encrypted = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY, null) ?: return null
        val json = decrypt(encrypted) ?: return null
        return try {
            val root = JSONObject(json)
            WidgetConfig(
                baseUrl = root.getString("baseUrl"),
                token = root.getString("token"),
                deviceId = root.getString("deviceId"),
                name = root.optString("name", "Sentinel"),
                totp = root.optString("totp", ""),
                allowWithoutUnlock = root.optBoolean("allowWithoutUnlock", false),
                showCode = root.optBoolean("showCode", true),
            )
        } catch (_: Exception) {
            null
        }
    }

    fun clear(context: Context) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().remove(KEY).apply()
    }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getEntry(ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        val spec = KeyGenParameterSpec.Builder(
            ALIAS,
            KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
        )
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            .build()
        generator.init(spec)
        return generator.generateKey()
    }

    private fun encrypt(text: String): String? = try {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val iv = cipher.iv
        val data = cipher.doFinal(text.toByteArray(Charsets.UTF_8))
        Base64.encodeToString(iv + data, Base64.NO_WRAP)
    } catch (_: Exception) {
        null
    }

    private fun decrypt(blob: String): String? = try {
        val raw = Base64.decode(blob, Base64.NO_WRAP)
        val iv = raw.copyOfRange(0, 12)
        val data = raw.copyOfRange(12, raw.size)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
        String(cipher.doFinal(data), Charsets.UTF_8)
    } catch (_: Exception) {
        null
    }
}
