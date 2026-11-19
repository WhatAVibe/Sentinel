#pragma once
#include <furi.h>
#include <furi_hal.h>

/*
 * Transport for the Sentinel Flipper app.
 *
 * BLE: exposes the official Flipper BLE serial profile (same service qFlipper
 * uses). The Sentinel phone app connects to it and bridges commands to the
 * relay. No credentials are stored on the Flipper in this mode.
 *
 * UART: 115200 on GPIO 13 (TX) / 14 (RX) to an ESP32 running
 * esp32/sentinel_bridge.ino, for standalone use without a phone.
 */

typedef void (*SentinelLinkRxCallback)(const char* line, void* context);

typedef enum {
    SentinelLinkModeBle,
    SentinelLinkModeUart,
} SentinelLinkMode;

typedef struct SentinelLink SentinelLink;

SentinelLink* sentinel_link_alloc(
    SentinelLinkRxCallback rx_callback,
    void* context,
    SentinelLinkMode mode);

void sentinel_link_free(SentinelLink* link);

// Switch transport. Stops the current one first; safe to call from the UI thread.
void sentinel_link_set_mode(SentinelLink* link, SentinelLinkMode mode);
SentinelLinkMode sentinel_link_mode(SentinelLink* link);
bool sentinel_link_ble_connected(SentinelLink* link);

// Keep-alive, no error reporting. Only does anything on an established BLE link.
void sentinel_link_ping(SentinelLink* link);

// Send a newline-terminated line over the active transport.
bool sentinel_link_send(SentinelLink* link, const char* line);
