package com.pcmonitor.sentinelwatch

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

object TileActions {
    val pending = MutableStateFlow<String?>(null)
    val screen = MutableStateFlow<String?>(null)
}

private val SentinelColors = darkColorScheme(
    primary = Color(0xFF6C8CFF),
    onPrimary = Color.White,
    surface = Color(0xFF161B23),
    onSurface = Color(0xFFE6E9EF),
    background = Color(0xFF0B0D11),
    onBackground = Color(0xFFE6E9EF),
    error = Color(0xFFFF6B6B),
)

private val Muted = Color(0xFF8B93A1)

class MainActivity : ComponentActivity() {
    companion object {
        const val EXTRA_SCREEN = "sentinel_screen"
        const val SCREEN_CODE = "code"
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        readIntent(intent)
        setContent {
            MaterialTheme(colorScheme = SentinelColors) {
                Surface(color = Color(0xFF0B0D11), modifier = Modifier.fillMaxSize()) {
                    WatchApp()
                }
            }
        }
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        readIntent(intent)
    }

    private fun readIntent(intent: android.content.Intent?) {
        TileActions.pending.value = intent?.getStringExtra("sentinel_action")
        intent?.getStringExtra(EXTRA_SCREEN)?.let { TileActions.screen.value = it }
    }
}

@Composable
private fun WatchApp() {
    val context = LocalContext.current
    val store = remember { Store(context) }
    var session by remember { mutableStateOf(store.load()) }
    var screen by remember { mutableStateOf(if (session == null) "pair" else "home") }
    val pending by TileActions.pending.collectAsState()
    val requestedScreen by TileActions.screen.collectAsState()
    val active = session

    LaunchedEffect(requestedScreen, active) {
        if (requestedScreen != null && active != null) {
            screen = requestedScreen!!
            TileActions.screen.value = null
        }
    }

    if (active != null && pending != null) {
        QuickActionScreen(pending!!, active) { TileActions.pending.value = null }
        return
    }

    when (screen) {
        "pair" -> PairScreen(store) { paired ->
            session = paired
            screen = "home"
        }
        "code" -> session?.let { CodeScreen(it.secret) { screen = "home" } }
        "settings" -> session?.let {
            SettingsScreen(
                session = it,
                onBack = { screen = "home" },
                onTiles = { screen = "tiles" },
                onUnpair = {
                    store.clear()
                    session = null
                    screen = "pair"
                },
            )
        }
        "tiles" -> TileSettingsScreen(store) { screen = "settings" }
        else -> session?.let {
            HomeScreen(
                session = it,
                onCode = { screen = "code" },
                onSettings = { screen = "settings" },
            )
        }
    }
}

@Composable
private fun PairScreen(store: Store, onPaired: (Session) -> Unit) {
    var server by remember { mutableStateOf("") }
    var code by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text("Sentinel", fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
        Text("Pair this watch", fontSize = 11.sp, color = Muted)
        OutlinedTextField(
            value = server,
            onValueChange = { server = it },
            label = { Text("Server") },
            singleLine = true,
            textStyle = TextStyle(fontSize = 11.sp),
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            value = code,
            onValueChange = { code = it.uppercase().filter { c -> c.isLetterOrDigit() }.take(6) },
            label = { Text("Pair code") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Characters),
            textStyle = TextStyle(fontSize = 16.sp, letterSpacing = 3.sp, fontFamily = FontFamily.Monospace),
            modifier = Modifier.fillMaxWidth(),
        )
        Button(
            onClick = {
                scope.launch {
                    busy = true
                    error = null
                    try {
                        val base = server.trim().trimEnd('/')
                        val api = Api(base)
                        val paired = withContext(Dispatchers.IO) {
                            val requestId = api.lookup(code)
                            api.approve(requestId, code, DEVICE_NAME, store.uid())
                        }
                        val session = Session(
                            baseUrl = base,
                            token = paired.token,
                            secret = paired.secret,
                            deviceId = paired.deviceId,
                            deviceName = paired.deviceName,
                        )
                        store.save(session)
                        onPaired(session)
                    } catch (e: Exception) {
                        error = friendlyError(e)
                    } finally {
                        busy = false
                    }
                }
            },
            enabled = !busy && code.length == 6,
            modifier = Modifier.fillMaxWidth(),
        ) {
            Text(if (busy) "Pairing..." else "Pair")
        }
        if (busy) CircularProgressIndicator(modifier = Modifier.height(20.dp))
        error?.let {
            Text(it, color = MaterialTheme.colorScheme.error, fontSize = 11.sp, textAlign = TextAlign.Center)
        }
        Text(
            "PC: Settings -> Pair another phone, then type its code here.",
            fontSize = 9.sp,
            color = Muted,
            textAlign = TextAlign.Center,
        )
    }
}

@Composable
private fun HomeScreen(session: Session, onCode: () -> Unit, onSettings: () -> Unit) {
    val api = remember(session) { Api(session.baseUrl, session.token) }
    var status by remember { mutableStateOf<DeviceStatus?>(null) }
    var message by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    suspend fun refresh() {
        try {
            status = withContext(Dispatchers.IO) { api.status(session.deviceId) }
        } catch (e: Exception) {
            message = friendlyError(e)
        }
    }

    fun run(type: String, args: JSONObject? = null, needsCode: Boolean = false) {
        scope.launch {
            busy = true
            try {
                withContext(Dispatchers.IO) {
                    api.command(
                        session.deviceId,
                        type,
                        args,
                        if (needsCode) Totp.now(session.secret) else null,
                    )
                }
                message = "Sent: $type"
                refresh()
            } catch (e: Exception) {
                message = friendlyError(e)
            } finally {
                busy = false
            }
        }
    }

    LaunchedEffect(Unit) {
        while (true) {
            refresh()
            delay(10000)
        }
    }

    val online = status?.online == true
    val away = status?.away == true
    val lockedView = status?.secureLock == true

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(14.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(7.dp),
    ) {
        Text(status?.name ?: session.deviceName, fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
        Text(
            when {
                !online -> "Offline"
                lockedView -> "Secure lock"
                away -> "Away mode"
                else -> "Online"
            },
            fontSize = 11.sp,
            color = when {
                !online -> Color(0xFFF2C46D)
                lockedView || away -> Color(0xFFF2C46D)
                else -> Color(0xFF6EE7A8)
            },
        )

        if (status == null) {
            CircularProgressIndicator(modifier = Modifier.height(22.dp))
        }

        LiveCode(session.secret)

        if (lockedView) {
            ActionButton("Unlock screen", enabled = online && !busy) { run("unlock_screen", needsCode = true) }
        }
        ActionButton("Lock PC", enabled = online && !busy) { run("lock") }
        ActionButton("Secure lock", enabled = online && !busy) {
            run("lock_screen", JSONObject().put("message", "PC locked from your watch."))
        }
        if (away) {
            ActionButton("Disarm away", enabled = online && !busy) { run("away_disable", needsCode = true) }
        } else {
            ActionButton("Away mode", enabled = online && !busy) { run("away_enable") }
        }
        ActionButton("Screenshot", enabled = online && !busy) { run("screenshot") }
        ActionButton("Ring PC", enabled = online && !busy) { run("alarm") }

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(onClick = onCode, modifier = Modifier.weight(1f)) { Text("Code", fontSize = 11.sp) }
            Button(onClick = onSettings, modifier = Modifier.weight(1f)) { Text("Settings", fontSize = 11.sp) }
        }

        if (message.isNotEmpty()) {
            Spacer(Modifier.height(2.dp))
            Text(message, fontSize = 9.sp, color = Muted, textAlign = TextAlign.Center)
        }
    }
}

@Composable
private fun LiveCode(secret: String) {
    var code by remember { mutableStateOf(Totp.now(secret)) }
    var seconds by remember { mutableStateOf(Totp.secondsRemaining()) }

    LaunchedEffect(secret) {
        while (true) {
            code = Totp.now(secret)
            seconds = Totp.secondsRemaining()
            delay(1000)
        }
    }

    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Text(
            formatCode(code),
            fontSize = 26.sp,
            fontFamily = FontFamily.Monospace,
            fontWeight = FontWeight.SemiBold,
            letterSpacing = 2.sp,
        )
        LinearProgressIndicator(
            progress = { seconds / 30f },
            modifier = Modifier
                .fillMaxWidth()
                .height(4.dp),
            color = Color(0xFF6C8CFF),
            trackColor = Color(0xFF2B3444),
        )
        Text("$seconds s", fontSize = 9.sp, color = Muted)
    }
}

@Composable
private fun ActionButton(label: String, enabled: Boolean = true, onClick: () -> Unit) {
    Button(onClick = onClick, enabled = enabled, modifier = Modifier.fillMaxWidth()) {
        Text(label, fontSize = 12.sp)
    }
}

@Composable
private fun CodeScreen(secret: String, onBack: () -> Unit) {
    var code by remember { mutableStateOf(Totp.now(secret)) }
    var seconds by remember { mutableStateOf(Totp.secondsRemaining()) }

    LaunchedEffect(Unit) {
        while (true) {
            code = Totp.now(secret)
            seconds = Totp.secondsRemaining()
            delay(1000)
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text("2FA code", fontSize = 11.sp, color = Muted)
        Text(
            code.chunked(3).joinToString(" "),
            fontSize = 30.sp,
            fontFamily = FontFamily.Monospace,
            fontWeight = FontWeight.SemiBold,
            letterSpacing = 2.sp,
        )
        Text("$seconds s", fontSize = 11.sp, color = Muted)
        Spacer(Modifier.height(12.dp))
        Button(onClick = onBack, modifier = Modifier.fillMaxWidth()) { Text("Back") }
    }
}

@Composable
private fun SettingsScreen(session: Session, onBack: () -> Unit, onTiles: () -> Unit, onUnpair: () -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text("Settings", fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
        Text(session.deviceName, fontSize = 12.sp)
        Text(session.baseUrl, fontSize = 9.sp, color = Muted, textAlign = TextAlign.Center)
        Button(onClick = onTiles, modifier = Modifier.fillMaxWidth()) { Text("Tile layout") }
        Text(
            "Complication: long-press the watch face, pick a slot, Sentinel.",
            fontSize = 9.sp,
            color = Muted,
            textAlign = TextAlign.Center,
        )
        Button(onClick = onBack, modifier = Modifier.fillMaxWidth()) { Text("Back") }
        Button(onClick = onUnpair, modifier = Modifier.fillMaxWidth()) {
            Text("Unpair this watch", color = Color(0xFFFF6B6B))
        }
        Text("Sentinel Watch 1.3.1", fontSize = 9.sp, color = Muted)
        Text("TOTP stays on this watch. Protect it with a screen lock.", fontSize = 8.sp, color = Muted,
            textAlign = TextAlign.Center)
    }
}

@Composable
private fun ToggleRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(label, fontSize = 11.sp, modifier = Modifier.weight(1f))
        Switch(checked = checked, onCheckedChange = onChange)
    }
}

@Composable
private fun TileSettingsScreen(store: Store, onBack: () -> Unit) {
    val context = LocalContext.current
    var config by remember { mutableStateOf(store.tileConfig()) }

    fun update(next: TileConfig) {
        config = next
        store.saveTileConfig(next)
        refreshSentinelTiles(context)
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(14.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Text("All-in-one tile", fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
        Text(
            "Dashboard tile rows and buttons.",
            fontSize = 9.sp,
            color = Muted,
            textAlign = TextAlign.Center,
        )
        ToggleRow("PC name", config.name) { update(config.copy(name = it)) }
        ToggleRow("Status", config.status) { update(config.copy(status = it)) }
        ToggleRow("2FA code", config.code) { update(config.copy(code = it)) }
        ToggleRow("Countdown", config.seconds) { update(config.copy(seconds = it)) }
        Text("Quick actions", fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
        TILE_ACTIONS.forEach { (id, label) ->
            ToggleRow(label, id in config.actions) { checked ->
                val actions = if (checked) config.actions + id else config.actions - id
                update(config.copy(actions = actions))
            }
        }
        Button(onClick = onBack, modifier = Modifier.fillMaxWidth()) { Text("Back") }
    }
}

@Composable
private fun QuickActionScreen(action: String, session: Session, onDone: () -> Unit) {
    val context = LocalContext.current
    var message by remember { mutableStateOf("Working...") }

    LaunchedEffect(action) {
        val failure = runCatching {
            withContext(Dispatchers.IO) {
                val api = Api(session.baseUrl, session.token)
                when (action) {
                    "lock" -> api.command(session.deviceId, "lock")
                    "unlock" -> api.command(session.deviceId, "unlock", code = Totp.now(session.secret))
                    "away" -> {
                        val status = api.status(session.deviceId)
                        if (status.away) {
                            api.command(session.deviceId, "away_disable", code = Totp.now(session.secret))
                        } else {
                            api.command(session.deviceId, "away_enable")
                        }
                    }
                    "secure" -> api.command(
                        session.deviceId,
                        "lock_screen",
                        JSONObject().put("message", "PC locked from your watch."),
                    )
                    "shot" -> api.command(session.deviceId, "screenshot")
                    "ring" -> api.command(session.deviceId, "alarm")
                }
            }
        }.exceptionOrNull()
        message = if (failure == null) "Done" else friendlyError(failure)
        refreshSentinelTiles(context)
        delay(1300)
        onDone()
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(18.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text("Sentinel", fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
        Text(action.replaceFirstChar { it.uppercase() }, fontSize = 11.sp, color = Muted)
        Spacer(Modifier.height(10.dp))
        Text(message, fontSize = 13.sp, textAlign = TextAlign.Center)
    }
}

