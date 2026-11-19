#include "sentinel_link.h"
#include <furi_hal_serial.h>
#include <furi_hal_serial_control.h>
#include <furi/core/stream_buffer.h>
#include <bt/bt_service/bt.h>
#include <profiles/serial_profile.h>
#include <string.h>
#include <stdio.h>

#define SENTINEL_UART_BAUD 115200
#define SENTINEL_LINE_MAX  256
#define SENTINEL_BLE_BUFFER 512

struct SentinelLink {
    SentinelLinkMode mode;
    FuriStreamBuffer* rx;
    FuriThread* thread;
    volatile bool running;

    FuriHalSerialHandle* uart;

    Bt* bt;
    FuriHalBleProfileBase* ble;
    volatile bool ble_connected;

    uint8_t buf[SENTINEL_LINE_MAX];
    size_t buf_len;

    SentinelLinkRxCallback rx_callback;
    void* rx_context;
};

static void sentinel_inject(SentinelLink* link, const char* line) {
    if(!link->rx || !line) return;
    furi_stream_buffer_send(link->rx, line, strlen(line), 0);
}

static int32_t sentinel_link_thread(void* context) {
    SentinelLink* link = context;
    uint8_t byte;
    while(link->running) {
        if(furi_stream_buffer_receive(link->rx, &byte, 1, 100) != 1) continue;
        if(byte == '\n' || byte == '\r') {
            if(link->buf_len > 0) {
                link->buf[link->buf_len] = '\0';
                if(link->rx_callback) link->rx_callback((const char*)link->buf, link->rx_context);
                link->buf_len = 0;
            }
        } else if(link->buf_len < SENTINEL_LINE_MAX - 1) {
            link->buf[link->buf_len++] = byte;
        }
    }
    return 0;
}

// ---- UART ---------------------------------------------------------

static void sentinel_uart_isr_cb(
    FuriHalSerialHandle* handle,
    FuriHalSerialRxEvent event,
    void* context) {
    SentinelLink* link = context;
    if(event != FuriHalSerialRxEventData) return;
    uint8_t byte = furi_hal_serial_async_rx(handle);
    furi_stream_buffer_send(link->rx, &byte, 1, 0);
}

static void sentinel_uart_start(SentinelLink* link) {
    link->uart = furi_hal_serial_control_acquire(FuriHalSerialIdUsart);
    if(!link->uart) {
        sentinel_inject(link, "ERR|UART busy\n");
        return;
    }
    furi_hal_serial_init(link->uart, SENTINEL_UART_BAUD);
    furi_hal_serial_async_rx_start(link->uart, sentinel_uart_isr_cb, link, false);
}

static void sentinel_uart_stop(SentinelLink* link) {
    if(!link->uart) return;
    furi_hal_serial_async_rx_stop(link->uart);
    furi_hal_serial_deinit(link->uart);
    furi_hal_serial_control_release(link->uart);
    link->uart = NULL;
}

// ---- BLE serial profile -------------------------------------------

static uint16_t sentinel_ble_event(SerialServiceEvent event, void* context) {
    SentinelLink* link = context;
    if(event.event == SerialServiceEventTypeDataReceived && event.data.size > 0) {
        furi_stream_buffer_send(link->rx, event.data.buffer, event.data.size, 0);
    } else if(event.event == SerialServiceEventTypesBleResetRequest) {
        sentinel_inject(link, "INFO|Bluetooth reset requested\n");
    }
    return SENTINEL_BLE_BUFFER;
}

static void sentinel_bt_status(BtStatus status, void* context) {
    SentinelLink* link = context;
    bool connected = status == BtStatusConnected;
    if(connected == link->ble_connected) return;
    link->ble_connected = connected;

    if(connected) {
        // The BT service hands the serial profile to qFlipper's RPC session as
        // soon as a central connects, which steals our receive callback and the
        // TX pipe. Claim it back so the phone bridge talks to us instead.
        if(link->ble) {
            ble_profile_serial_set_event_callback(
                link->ble, SENTINEL_BLE_BUFFER, sentinel_ble_event, link);
            ble_profile_serial_set_rpc_active(link->ble, false);
        }
    }
    sentinel_inject(
        link, connected ? "INFO|Bluetooth connected\n" : "INFO|Bluetooth disconnected\n");
}

static void sentinel_ble_start(SentinelLink* link) {
    link->bt = furi_record_open(RECORD_BT);
    if(!link->bt) {
        sentinel_inject(link, "ERR|Bluetooth service unavailable\n");
        return;
    }
    link->ble = bt_profile_start(link->bt, ble_profile_serial, NULL);
    if(!link->ble) {
        sentinel_inject(link, "ERR|Bluetooth serial profile unavailable\n");
        furi_record_close(RECORD_BT);
        link->bt = NULL;
        return;
    }
    bt_set_status_changed_callback(link->bt, sentinel_bt_status, link);
    ble_profile_serial_set_event_callback(link->ble, SENTINEL_BLE_BUFFER, sentinel_ble_event, link);
    ble_profile_serial_set_rpc_active(link->ble, false);
    sentinel_inject(link, "INFO|Bluetooth advertising\n");
}

static void sentinel_ble_stop(SentinelLink* link) {
    if(link->bt) {
        bt_set_status_changed_callback(link->bt, NULL, NULL);
        if(link->ble) {
            // Drop the peer first so the profile is never torn down mid-transfer.
            bt_disconnect(link->bt);
            bt_profile_restore_default(link->bt);
        }
    }
    link->ble = NULL;
    link->ble_connected = false;
    if(link->bt) {
        furi_record_close(RECORD_BT);
        link->bt = NULL;
    }
}

// ---- public -------------------------------------------------------

SentinelLink* sentinel_link_alloc(
    SentinelLinkRxCallback rx_callback,
    void* context,
    SentinelLinkMode mode) {
    SentinelLink* link = malloc(sizeof(SentinelLink));
    memset(link, 0, sizeof(SentinelLink));
    link->rx_callback = rx_callback;
    link->rx_context = context;
    link->running = true;
    link->rx = furi_stream_buffer_alloc(1024, 1);

    link->thread = furi_thread_alloc_ex("SentinelRx", 4 * 1024, sentinel_link_thread, link);
    furi_thread_start(link->thread);

    sentinel_link_set_mode(link, mode);
    return link;
}

void sentinel_link_set_mode(SentinelLink* link, SentinelLinkMode mode) {
    if(!link) return;
    if(link->mode == mode) {
        if(mode == SentinelLinkModeBle && link->bt) return;
        if(mode == SentinelLinkModeUart && link->uart) return;
    }

    sentinel_uart_stop(link);
    sentinel_ble_stop(link);
    link->mode = mode;

    if(mode == SentinelLinkModeBle) {
        sentinel_ble_start(link);
    } else {
        sentinel_uart_start(link);
        sentinel_inject(link, "INFO|UART 115200 on GPIO 13/14\n");
    }
}

SentinelLinkMode sentinel_link_mode(SentinelLink* link) {
    return link ? link->mode : SentinelLinkModeBle;
}

bool sentinel_link_ble_connected(SentinelLink* link) {
    return link && link->mode == SentinelLinkModeBle && link->ble_connected;
}

void sentinel_link_ping(SentinelLink* link) {
    if(!link || link->mode != SentinelLinkModeBle) return;
    if(!link->ble || !link->ble_connected) return;
    static uint8_t ping[] = "PING\n";
    ble_profile_serial_tx(link->ble, ping, sizeof(ping) - 1);
}

bool sentinel_link_send(SentinelLink* link, const char* line) {
    if(!link || !line) return false;

    if(link->mode == SentinelLinkModeUart) {
        if(!link->uart) {
            sentinel_inject(link, "ERR|UART not available\n");
            return false;
        }
        furi_hal_serial_tx(link->uart, (const uint8_t*)line, strlen(line));
        furi_hal_serial_tx(link->uart, (const uint8_t*)"\n", 1);
        return true;
    }

    if(!link->ble || !link->ble_connected) {
        sentinel_inject(link, "ERR|Bluetooth not connected\n");
        return false;
    }

    char buf[SENTINEL_LINE_MAX + 2];
    int length = snprintf(buf, sizeof(buf), "%s\n", line);
    if(length <= 0 || length > BLE_PROFILE_SERIAL_PACKET_SIZE_MAX) {
        sentinel_inject(link, "ERR|line too long for Bluetooth\n");
        return false;
    }
    if(!ble_profile_serial_tx(link->ble, (uint8_t*)buf, (uint16_t)length)) {
        sentinel_inject(link, "ERR|Bluetooth send failed\n");
        return false;
    }
    return true;
}

void sentinel_link_free(SentinelLink* link) {
    if(!link) return;
    sentinel_uart_stop(link);
    sentinel_ble_stop(link);
    link->running = false;
    if(link->thread) {
        furi_thread_join(link->thread);
        furi_thread_free(link->thread);
    }
    if(link->rx) furi_stream_buffer_free(link->rx);
    free(link);
}
