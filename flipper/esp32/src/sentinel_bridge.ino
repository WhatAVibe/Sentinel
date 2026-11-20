/*
 * Sentinel bridge — ESP32 side.
 *
 * Flash to an ESP32 devboard wired to the Flipper's GPIO header:
 *   Flipper 13 (TX) -> ESP32 RX
 *   Flipper 14 (RX) -> ESP32 TX
 *   GND             -> GND
 *
 * The ESP32 owns WiFi, HTTPS, the relay token and TOTP. The Flipper sends
 * short ASCII commands and renders the STATE/INFO lines printed here.
 *
 * Serial protocol (115200, newline-terminated):
 *   SSID:<value>        store WiFi SSID
 *   PASS:<value>        store WiFi password
 *   URL:<value>         store relay base URL (http:// or https://)
 *   CONNECT             connect WiFi, then poll if already paired
 *   PAIR:<code>         pair with the PC's six-character code
 *   FORGET              delete the relay token/device (keeps WiFi + URL)
 *   RESET               delete everything, including WiFi + URL
 *   STATUS              fetch and print the current state now
 *   LOCK                POST lock
 *   SECURE              POST lock_screen
 *   AWAY                toggle away_enable / away_disable
 *   UNLOCK              unlock_screen if secure-locked, otherwise unlock
 *   SHOT                POST screenshot
 *
 * Output:
 *   STATE|online|away|secure|session|code|seconds|name
 *   INFO|<text>
 *   ERR|<text>
 *
 * Requires ArduinoJson 7 (Library Manager) and the ESP32 Arduino core.
 */

#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <HTTPClient.h>
#include <Preferences.h>
#include <ArduinoJson.h>
#include <mbedtls/md.h>
#include <esp_system.h>
#include <time.h>

static const char* APP_VERSION = "0.2.0";
static const unsigned long POLL_MS = 15000;
static const unsigned long HTTP_TIMEOUT_MS = 10000;

Preferences prefs;
WiFiClient plainClient;
WiFiClientSecure secureClient;

String wifiSsid;
String wifiPass;
String baseUrl;
String token;
String deviceId;
String deviceName;
String totpSecret;
String mobileUid;

bool paired = false;
bool lastAway = false;
bool lastSecure = false;
unsigned long lastPoll = 0;

// ---------------------------------------------------------------- helpers

// Flipper link. The Flipper sends on GPIO13 (Flipper TX) to the ESP32 RX and
// listens on GPIO14; the official WiFi devboard maps UART0 to GPIO44/43.
#if defined(SENTINEL_ESP32S2)
#define FLIPPER_UART Serial0
#else
#define FLIPPER_UART Serial
#endif

void sendLine(const String& line) {
  FLIPPER_UART.println(line);
#if defined(SENTINEL_ESP32S2)
  Serial.println(line);
#endif
}
void info(const String& text) {
  sendLine("INFO|" + text);
}
void failure(const String& text) {
  sendLine("ERR|" + text);
}

String randomHex(size_t bytes) {
  static const char* hex = "0123456789abcdef";
  String out;
  out.reserve(bytes * 2);
  for(size_t i = 0; i < bytes; i++) {
    uint8_t value = (uint8_t)(esp_random() & 0xff);
    out += hex[value >> 4];
    out += hex[value & 0x0f];
  }
  return out;
}

String trimSlash(String value) {
  value.trim();
  while(value.endsWith("/")) value.remove(value.length() - 1);
  return value;
}

String endpoint(const String& path) {
  return trimSlash(baseUrl) + path;
}

void loadPrefs() {
  prefs.begin("sentinel", false);
  wifiSsid = prefs.getString("ssid", "");
  wifiPass = prefs.getString("pass", "");
  baseUrl = prefs.getString("url", "");
  token = prefs.getString("token", "");
  deviceId = prefs.getString("device", "");
  deviceName = prefs.getString("name", "");
  totpSecret = prefs.getString("secret", "");
  mobileUid = prefs.getString("uid", "");
  if(mobileUid.length() == 0) {
    mobileUid = randomHex(12);
    prefs.putString("uid", mobileUid);
  }
  if(baseUrl.length() == 0) {
    baseUrl = "https://sentinel.whatavibe.net";
    prefs.putString("url", baseUrl);
  }
  paired = token.length() > 0 && deviceId.length() > 0;
}

void savePrefs() {
  prefs.putString("ssid", wifiSsid);
  prefs.putString("pass", wifiPass);
  prefs.putString("url", baseUrl);
  prefs.putString("token", token);
  prefs.putString("device", deviceId);
  prefs.putString("name", deviceName);
  prefs.putString("secret", totpSecret);
  prefs.putString("uid", mobileUid);
}

// ---------------------------------------------------------------- TOTP

int base32Decode(const String& input, uint8_t* out, size_t maxLen) {
  int bits = 0;
  uint32_t value = 0;
  size_t written = 0;
  for(size_t i = 0; i < input.length(); i++) {
    char c = input.charAt(i);
    if(c == ' ' || c == '=' || c == '-') continue;
    int v;
    if(c >= 'a' && c <= 'z') c = (char)(c - 32);
    if(c >= 'A' && c <= 'Z') {
      v = c - 'A';
    } else if(c >= '2' && c <= '7') {
      v = 26 + (c - '2');
    } else {
      continue;
    }
    value = (value << 5) | (uint32_t)v;
    bits += 5;
    if(bits >= 8) {
      if(written >= maxLen) break;
      out[written++] = (uint8_t)((value >> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return (int)written;
}

String totpNow() {
  time_t now = time(nullptr);
  if(now < 1600000000) return "";
  if(totpSecret.length() == 0) return "";

  uint8_t key[64];
  int keyLen = base32Decode(totpSecret, key, sizeof(key));
  if(keyLen <= 0) return "";

  uint32_t counter = (uint32_t)(now / 30);
  uint8_t message[8];
  for(int i = 7; i >= 0; i--) {
    message[i] = (uint8_t)(counter & 0xff);
    counter >>= 8;
  }

  uint8_t digest[20];
  const mbedtls_md_info_t* md = mbedtls_md_info_from_type(MBEDTLS_MD_SHA1);
  if(md == nullptr) return "";
  if(mbedtls_md_hmac(md, key, (size_t)keyLen, message, sizeof(message), digest) != 0) return "";

  int offset = digest[19] & 0x0f;
  uint32_t binary = ((uint32_t)(digest[offset] & 0x7f) << 24) |
                    ((uint32_t)digest[offset + 1] << 16) |
                    ((uint32_t)digest[offset + 2] << 8) |
                    (uint32_t)digest[offset + 3];
  uint32_t code = binary % 1000000;
  char out[7];
  snprintf(out, sizeof(out), "%06lu", (unsigned long)code);
  return String(out);
}

// ---------------------------------------------------------------- HTTP

bool httpJson(
    const char* method,
    const String& path,
    const String& body,
    JsonDocument& doc,
    int& status) {
  status = -1;
  if(WiFi.status() != WL_CONNECTED) return false;
  if(baseUrl.length() == 0) return false;

  HTTPClient http;
  http.setTimeout(HTTP_TIMEOUT_MS);
  bool tls = trimSlash(baseUrl).startsWith("https://");
  if(tls) {
    secureClient.setInsecure();
    if(!http.begin(secureClient, endpoint(path))) return false;
  } else {
    if(!http.begin(plainClient, endpoint(path))) return false;
  }

  http.setUserAgent("SentinelFlipper/" + String(APP_VERSION));
  http.addHeader("Content-Type", "application/json");
  http.addHeader("X-Device-Name", "Flipper Zero");
  if(token.length() > 0) http.addHeader("Authorization", "Bearer " + token);

  status = (strcmp(method, "GET") == 0) ? http.GET() : http.POST(body);
  String payload = http.getString();
  http.end();

  if(status >= 200 && status < 300) {
    DeserializationError error = deserializeJson(doc, payload);
    return !error;
  }

  JsonDocument err;
  if(!deserializeJson(err, payload)) {
    String code = err["error"].as<String>();
    if(code.length() > 0) failure(code);
  }
  return false;
}

bool sendCommand(const String& type, const String& code = "", const String& message = "") {
  if(!paired) {
    failure("not paired");
    return false;
  }
  String body = "{\"type\":\"" + type + "\"";
  if(code.length() > 0) body += ",\"code\":\"" + code + "\"";
  if(message.length() > 0) body += ",\"args\":{\"message\":\"" + message + "\"}";
  body += "}";

  JsonDocument doc;
  int status = 0;
  if(!httpJson("POST", "/api/v1/devices/" + deviceId + "/command", body, doc, status)) {
    if(status == 401) {
      token = "";
      paired = false;
      savePrefs();
      failure("token rejected — pair again");
    } else if(status > 0) {
      failure("command failed (HTTP " + String(status) + ")");
    } else {
      failure("network error");
    }
    return false;
  }
  info("sent " + type);
  return true;
}

// ---------------------------------------------------------------- WiFi / time

void ensureTime() {
  if(time(nullptr) >= 1600000000) return;
  configTime(0, 0, "pool.ntp.org", "time.google.com");
  for(int i = 0; i < 40 && time(nullptr) < 1600000000; i++) delay(250);
}

bool connectWifi() {
  if(WiFi.status() == WL_CONNECTED) {
    ensureTime();
    return true;
  }
  if(wifiSsid.length() == 0) {
    failure("set WiFi SSID first");
    return false;
  }

  WiFi.mode(WIFI_STA);
  WiFi.begin(wifiSsid.c_str(), wifiPass.c_str());
  for(int i = 0; i < 60 && WiFi.status() != WL_CONNECTED; i++) delay(250);
  if(WiFi.status() != WL_CONNECTED) {
    failure("WiFi connection failed");
    return false;
  }
  info("WiFi " + WiFi.localIP().toString());
  ensureTime();
  return true;
}

// ---------------------------------------------------------------- relay flow

bool pairDevice(const String& rawCode) {
  String code = rawCode;
  code.trim();
  code.toUpperCase();
  if(code.length() != 6) {
    failure("pair code must be 6 characters");
    return false;
  }
  if(baseUrl.length() == 0) {
    failure("set relay URL first");
    return false;
  }
  if(!connectWifi()) return false;

  JsonDocument lookup;
  int status = 0;
  String lookupBody = "{\"pair_code\":\"" + code + "\"}";
  if(!httpJson("POST", "/api/v1/pair/lookup", lookupBody, lookup, status)) {
    failure("pair lookup failed (HTTP " + String(status) + ")");
    return false;
  }
  String requestId = lookup["request_id"].as<String>();
  if(requestId.length() == 0) {
    failure("pair lookup returned no request");
    return false;
  }
  String pcName = lookup["device_name"].as<String>();

  JsonDocument result;
  String approveBody = "{\"request_id\":\"" + requestId + "\",\"pair_code\":\"" + code +
                       "\",\"mobile_name\":\"Flipper Zero\",\"mobile_uid\":\"" + mobileUid + "\"}";
  if(!httpJson("POST", "/api/v1/pair/approve", approveBody, result, status)) {
    failure("pair approve failed (HTTP " + String(status) + ")");
    return false;
  }

  token = result["mobile_token"].as<String>();
  totpSecret = result["totp_secret"].as<String>();
  JsonObject device = result["device"];
  deviceId = device["id"].as<String>();
  deviceName = device["name"].as<String>();
  if(deviceName.length() == 0) deviceName = pcName;
  paired = token.length() > 0 && deviceId.length() > 0;
  savePrefs();

  if(!paired) {
    failure("pairing returned no token");
    return false;
  }
  info("paired with " + deviceName);
  return true;
}

bool fetchStatus() {
  if(!paired) {
    failure("not paired");
    return false;
  }
  ensureTime();
  JsonDocument doc;
  int status = 0;
  if(!httpJson("GET", "/api/v1/devices/" + deviceId, "", doc, status)) {
    if(status == 401) {
      token = "";
      paired = false;
      savePrefs();
      failure("token rejected — pair again");
    } else if(status > 0) {
      failure("status failed (HTTP " + String(status) + ")");
    } else {
      failure("network error");
    }
    return false;
  }

  JsonObject device = doc["device"];
  bool online = device["online"].as<bool>();
  bool away = device["away"].as<bool>();
  bool secure = device["state"]["secure_lock"].as<bool>();
  String session = device["state"]["session"].as<String>();
  String name = device["name"].as<String>();
  int left = device["totp_seconds_remaining"].as<int>();
  if(name.length() > 0) deviceName = name;

  lastAway = away;
  lastSecure = secure;

  String code = totpNow();
  sendLine(
      "STATE|" + String(online ? 1 : 0) + "|" + String(away ? 1 : 0) + "|" +
      String(secure ? 1 : 0) + "|" + session + "|" + code + "|" + String(left) + "|" +
      deviceName);
  return true;
}

// ---------------------------------------------------------------- serial protocol

void handleCommand(String line) {
  line.trim();
  if(line.length() == 0) return;

  if(line.startsWith("SSID:")) {
    String value = line.substring(5);
    if(value != wifiSsid && WiFi.status() == WL_CONNECTED) WiFi.disconnect(true);
    wifiSsid = value;
    savePrefs();
    info("SSID stored");
  } else if(line.startsWith("PASS:")) {
    String value = line.substring(5);
    if(value != wifiPass && WiFi.status() == WL_CONNECTED) WiFi.disconnect(true);
    wifiPass = value;
    savePrefs();
    info("password stored");
  } else if(line.startsWith("URL:")) {
    baseUrl = trimSlash(line.substring(4));
    savePrefs();
    info("relay URL stored");
  } else if(line == "CONNECT") {
    if(connectWifi()) {
      if(paired) {
        info("connected as " + deviceName);
        fetchStatus();
      } else {
        info("WiFi ready — not paired, use Pair");
      }
    }
  } else if(line.startsWith("PAIR:")) {
    if(pairDevice(line.substring(5))) fetchStatus();
  } else if(line == "FORGET") {
    token = "";
    deviceId = "";
    deviceName = "";
    totpSecret = "";
    paired = false;
    savePrefs();
    info("pairing forgotten");
  } else if(line == "RESET") {
    prefs.clear();
    wifiSsid = "";
    wifiPass = "";
    baseUrl = "";
    token = "";
    deviceId = "";
    deviceName = "";
    totpSecret = "";
    paired = false;
    loadPrefs();
    info("all settings cleared");
  } else if(line == "STATUS") {
    if(connectWifi()) fetchStatus();
  } else if(line == "LOCK") {
    if(connectWifi() && sendCommand("lock")) { delay(1200); fetchStatus(); }
  } else if(line == "SECURE") {
    if(connectWifi() && sendCommand("lock_screen", "", "Locked from Flipper")) { delay(1200); fetchStatus(); }
  } else if(line == "SHOT") {
    if(connectWifi() && sendCommand("screenshot")) { delay(1200); fetchStatus(); }
  } else if(line == "UNLOCK") {
    String code = totpNow();
    if(code.length() == 0) {
      failure("no TOTP yet — wait for time sync");
      return;
    }
    if(connectWifi() && sendCommand(lastSecure ? "unlock_screen" : "unlock", code)) { delay(1200); fetchStatus(); }
  } else if(line == "AWAY") {
    if(!connectWifi()) return;
    if(lastAway) {
      String code = totpNow();
      if(code.length() == 0) {
        failure("no TOTP yet — wait for time sync");
        return;
      }
      if(sendCommand("away_disable", code)) { delay(1200); fetchStatus(); }
    } else {
      if(sendCommand("away_enable")) { delay(1200); fetchStatus(); }
    }
  } else {
    failure("unknown command");
  }
}

void setup() {
#if defined(SENTINEL_ESP32S2)
  // USB CDC stays available for logs and esptool; the Flipper talks on UART0.
  Serial.begin(115200);
  FLIPPER_UART.begin(115200, SERIAL_8N1, 44, 43);
#else
  FLIPPER_UART.begin(115200);
#endif
  FLIPPER_UART.setTimeout(200);
  loadPrefs();
  sendLine("INFO|Sentinel bridge " + String(APP_VERSION));
  if(wifiSsid.length() > 0) {
    connectWifi();
  }
  if(paired && WiFi.status() == WL_CONNECTED) fetchStatus();
}

void pump(Stream& port) {
  if(!port.available()) return;
  String line = port.readStringUntil('\n');
  line.replace("\r", "");
  line.trim();
  if(line.length() == 0) return;
  handleCommand(line);
}

void loop() {
  pump(FLIPPER_UART);
#if defined(SENTINEL_ESP32S2)
  // The USB port mirrors the protocol so the bridge can be configured and
  // checked from a serial terminal without the Flipper attached.
  pump(Serial);
#endif

  if(paired && WiFi.status() == WL_CONNECTED && millis() - lastPoll > POLL_MS) {
    lastPoll = millis();
    fetchStatus();
  }
}
