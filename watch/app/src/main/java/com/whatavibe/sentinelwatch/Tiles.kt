package com.whatavibe.sentinelwatch

import android.content.Context
import androidx.wear.tiles.ActionBuilders
import androidx.wear.tiles.ColorBuilders
import androidx.wear.tiles.DeviceParametersBuilders
import androidx.wear.tiles.DimensionBuilders
import androidx.wear.tiles.LayoutElementBuilders
import androidx.wear.tiles.ModifiersBuilders
import androidx.wear.tiles.RequestBuilders
import androidx.wear.tiles.ResourceBuilders as TileResources
import androidx.wear.tiles.TileBuilders
import androidx.wear.tiles.TileService
import androidx.wear.protolayout.ResourceBuilders as ProtoResources
import androidx.wear.tiles.material.CompactChip
import androidx.wear.tiles.material.layouts.PrimaryLayout
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext

private const val ACTION_KEY = "sentinel_action"

private const val WHITE = 0xFFFFFFFF.toInt()
private const val MUTED = 0xFF9AA3B2.toInt()
private const val GREEN = 0xFF6EE7A8.toInt()
private const val AMBER = 0xFFF2C46D.toInt()
private const val CHIP_BG = 0xFF2B3444.toInt()

val TILE_ACTIONS = listOf(
    "lock" to "Lock",
    "unlock" to "Unlock",
    "away" to "Away",
    "secure" to "Secure",
    "shot" to "Shot",
    "ring" to "Ring",
    "copy" to "Copy",
)

internal data class TileSnapshot(
    val paired: Boolean,
    val name: String,
    val online: Boolean,
    val away: Boolean,
    val secureLock: Boolean,
    val code: String,
    val seconds: Int,
)

internal fun loadSnapshot(context: Context): TileSnapshot {
    val session = Store(context).load()
        ?: return TileSnapshot(false, "Sentinel", false, false, false, "------", 0)
    val code = Totp.now(session.secret)
    val seconds = Totp.secondsRemaining()
    val status = runBlocking {
        withContext(Dispatchers.IO) {
            runCatching { Api(session.baseUrl, session.token, 4000).status(session.deviceId) }.getOrNull()
        }
    }
    return TileSnapshot(
        paired = true,
        name = status?.name ?: session.deviceName,
        online = status?.online == true,
        away = status?.away == true,
        secureLock = status?.secureLock == true,
        code = code,
        seconds = seconds,
    )
}

private fun deviceParams(request: RequestBuilders.TileRequest): DeviceParametersBuilders.DeviceParameters =
    request.deviceParameters ?: DeviceParametersBuilders.DeviceParameters.Builder()
        .setScreenWidthDp(192)
        .setScreenHeightDp(192)
        .setScreenDensity(2.5f)
        .build()

private fun font(size: Float, color: Int, weight: Int): LayoutElementBuilders.FontStyle =
    LayoutElementBuilders.FontStyle.Builder()
        .setSize(DimensionBuilders.sp(size))
        .setColor(ColorBuilders.argb(color))
        .setWeight(weight)
        .build()

private fun line(
    text: String,
    size: Float,
    color: Int = WHITE,
    weight: Int = LayoutElementBuilders.FONT_WEIGHT_NORMAL,
): LayoutElementBuilders.LayoutElement =
    LayoutElementBuilders.Text.Builder()
        .setText(text)
        .setMaxLines(1)
        .setFontStyle(font(size, color, weight))
        .build()

private fun gap(height: Float): LayoutElementBuilders.LayoutElement =
    LayoutElementBuilders.Spacer.Builder().setHeight(DimensionBuilders.dp(height)).build()

private fun clickable(context: Context, action: String?): ModifiersBuilders.Clickable {
    val activity = ActionBuilders.AndroidActivity.Builder()
        .setPackageName(context.packageName)
        .setClassName(MainActivity::class.java.name)
    if (action != null) {
        activity.addKeyToExtraMapping(
            ACTION_KEY,
            ActionBuilders.AndroidStringExtra.Builder().setValue(action).build(),
        )
    }
    val launch = ActionBuilders.LaunchAction.Builder()
        .setAndroidActivity(activity.build())
        .build()
    return ModifiersBuilders.Clickable.Builder().setOnClick(launch).build()
}

private fun pill(context: Context, label: String, action: String?): LayoutElementBuilders.LayoutElement {
    val background = ModifiersBuilders.Background.Builder()
        .setColor(ColorBuilders.argb(CHIP_BG))
        .setCorner(ModifiersBuilders.Corner.Builder().setRadius(DimensionBuilders.dp(14f)).build())
        .build()
    val padding = ModifiersBuilders.Padding.Builder()
        .setStart(DimensionBuilders.dp(11f))
        .setEnd(DimensionBuilders.dp(11f))
        .setTop(DimensionBuilders.dp(5f))
        .setBottom(DimensionBuilders.dp(5f))
        .build()
    return LayoutElementBuilders.Text.Builder()
        .setText(label)
        .setMaxLines(1)
        .setFontStyle(font(11f, WHITE, LayoutElementBuilders.FONT_WEIGHT_MEDIUM))
        .setModifiers(
            ModifiersBuilders.Modifiers.Builder()
                .setClickable(clickable(context, action))
                .setBackground(background)
                .setPadding(padding)
                .build(),
        )
        .build()
}

private fun chipRow(context: Context, chips: List<Pair<String, String>>): LayoutElementBuilders.LayoutElement {
    val row = LayoutElementBuilders.Row.Builder()
        .setVerticalAlignment(LayoutElementBuilders.VERTICAL_ALIGN_CENTER)
    chips.forEachIndexed { index, pair ->
        if (index > 0) {
            row.addContent(
                LayoutElementBuilders.Spacer.Builder().setWidth(DimensionBuilders.dp(6f)).build(),
            )
        }
        row.addContent(pill(context, pair.second, pair.first))
    }
    return row.build()
}

private fun openChip(
    context: Context,
    device: DeviceParametersBuilders.DeviceParameters,
    label: String = "Open Sentinel",
): LayoutElementBuilders.LayoutElement =
    CompactChip.Builder(context, label, clickable(context, null), device).build()

private fun content(children: List<LayoutElementBuilders.LayoutElement>): LayoutElementBuilders.LayoutElement {
    val column = LayoutElementBuilders.Column.Builder()
        .setWidth(DimensionBuilders.expand())
        .setHorizontalAlignment(LayoutElementBuilders.HORIZONTAL_ALIGN_CENTER)
    children.forEach { column.addContent(it) }
    return column.build()
}

private fun tile(
    context: Context,
    device: DeviceParametersBuilders.DeviceParameters,
    body: LayoutElementBuilders.LayoutElement,
): TileBuilders.Tile {
    val layout = PrimaryLayout.Builder(device)
        .setPrimaryChipContent(openChip(context, device))
        .setContent(body)
        .build()
    val legacyTimeline = androidx.wear.tiles.TimelineBuilders.Timeline.fromLayoutElement(layout)
    val modernTimeline = androidx.wear.protolayout.TimelineBuilders.Timeline.fromProto(legacyTimeline.toProto())
    return TileBuilders.Tile.Builder()
        .setResourcesVersion("1")
        .setFreshnessIntervalMillis(30_000)
        .setTileTimeline(modernTimeline)
        .build()
}

internal fun formatCode(code: String): String =
    if (code.length == 6) "${code.substring(0, 3)} ${code.substring(3)}" else code

private fun statusText(snapshot: TileSnapshot): Pair<String, Int> = when {
    !snapshot.paired -> "Pair in the app" to AMBER
    !snapshot.online -> "Offline" to AMBER
    snapshot.secureLock -> "Secure lock" to AMBER
    snapshot.away -> "Away mode" to AMBER
    else -> "Online" to GREEN
}

private fun actionsFor(ids: List<String>): List<Pair<String, String>> =
    TILE_ACTIONS.filter { it.first in ids }

private fun chunked(chips: List<Pair<String, String>>, size: Int): List<List<Pair<String, String>>> =
    if (chips.isEmpty()) emptyList() else List((chips.size + size - 1) / size) { i ->
        chips.subList(i * size, minOf((i + 1) * size, chips.size))
    }

fun refreshSentinelTiles(context: Context) {
    val updater = TileService.getUpdater(context)
    listOf(
        StatusTileService::class.java,
        DashboardTileService::class.java,
        ActionsTileService::class.java,
        CodeTileService::class.java,
    ).forEach { service -> runCatching { updater.requestUpdate(service) } }
}

abstract class SentinelTileService : TileService() {
    override fun onTileResourcesRequest(requestParams: RequestBuilders.ResourcesRequest) =
        Futures.immediateFuture(ProtoResources.Resources.Builder().setVersion("1").build())

    override fun onResourcesRequest(requestParams: RequestBuilders.ResourcesRequest) =
        Futures.immediateFuture(TileResources.Resources.Builder().setVersion("1").build())

    protected fun build(
        requestParams: RequestBuilders.TileRequest,
        children: List<LayoutElementBuilders.LayoutElement>,
    ): ListenableFuture<TileBuilders.Tile> {
        val context = applicationContext
        return Futures.immediateFuture(tile(context, deviceParams(requestParams), content(children)))
    }
}

class StatusTileService : SentinelTileService() {
    override fun onTileRequest(requestParams: RequestBuilders.TileRequest): ListenableFuture<TileBuilders.Tile> {
        val snapshot = loadSnapshot(applicationContext)
        val status = statusText(snapshot)
        return build(
            requestParams,
            listOf(
                line(snapshot.name, 13f, WHITE, LayoutElementBuilders.FONT_WEIGHT_BOLD),
                line(status.first, 11f, status.second),
                gap(4f),
                line(if (snapshot.paired) formatCode(snapshot.code) else "------", 26f, WHITE, LayoutElementBuilders.FONT_WEIGHT_BOLD),
                line(if (snapshot.paired) "${snapshot.seconds}s left" else "open to pair", 9f, MUTED),
                gap(4f),
                chipRow(applicationContext, listOf("lock" to "Lock", "unlock" to "Unlock")),
            ),
        )
    }
}

class DashboardTileService : SentinelTileService() {
    override fun onTileRequest(requestParams: RequestBuilders.TileRequest): ListenableFuture<TileBuilders.Tile> {
        val context = applicationContext
        val snapshot = loadSnapshot(context)
        val config = Store(context).tileConfig()
        val status = statusText(snapshot)
        val children = mutableListOf<LayoutElementBuilders.LayoutElement>()

        if (config.name) children += line(snapshot.name, 12f, WHITE, LayoutElementBuilders.FONT_WEIGHT_BOLD)
        if (config.status) children += line(status.first, 10f, status.second)
        if (config.code) {
            if (children.isNotEmpty()) children += gap(3f)
            children += line(
                if (snapshot.paired) formatCode(snapshot.code) else "------",
                24f,
                WHITE,
                LayoutElementBuilders.FONT_WEIGHT_BOLD,
            )
        }
        if (config.seconds) {
            children += line(if (snapshot.paired) "${snapshot.seconds}s left" else "open to pair", 9f, MUTED)
        }

        val chips = actionsFor(config.actions)
        if (chips.isNotEmpty()) {
            children += gap(4f)
            chunked(chips, if (chips.size > 4) 3 else 2).forEach { children += chipRow(context, it) }
        }
        if (children.isEmpty()) {
            children += line("Open Sentinel", 13f, WHITE, LayoutElementBuilders.FONT_WEIGHT_BOLD)
        }
        return build(requestParams, children)
    }
}

class ActionsTileService : SentinelTileService() {
    override fun onTileRequest(requestParams: RequestBuilders.TileRequest): ListenableFuture<TileBuilders.Tile> {
        val context = applicationContext
        val config = Store(context).tileConfig()
        val chips = actionsFor(config.actions).ifEmpty { actionsFor(listOf("lock", "away", "secure", "shot")) }
        val children = mutableListOf<LayoutElementBuilders.LayoutElement>(
            line("Quick actions", 12f, WHITE, LayoutElementBuilders.FONT_WEIGHT_BOLD),
            gap(4f),
        )
        chunked(chips, 2).forEach { children += chipRow(context, it) }
        return build(requestParams, children)
    }
}

class CodeTileService : SentinelTileService() {
    override fun onTileRequest(requestParams: RequestBuilders.TileRequest): ListenableFuture<TileBuilders.Tile> {
        val snapshot = loadSnapshot(applicationContext)
        return build(
            requestParams,
            listOf(
                line("2FA code", 11f, MUTED),
                line(if (snapshot.paired) formatCode(snapshot.code) else "------", 30f, WHITE, LayoutElementBuilders.FONT_WEIGHT_BOLD),
                line(if (snapshot.paired) "${snapshot.seconds}s left" else "pair the app", 9f, MUTED),
            ),
        )
    }
}
