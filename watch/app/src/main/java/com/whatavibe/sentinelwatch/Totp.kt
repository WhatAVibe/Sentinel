package com.whatavibe.sentinelwatch

import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

object Totp {
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

    fun secondsRemaining(): Int {
        val seconds = System.currentTimeMillis() / 1000
        return STEP - (seconds % STEP).toInt()
    }

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
