#include <furi.h>
#include <gui/gui.h>
#include <gui/view_dispatcher.h>
#include <gui/modules/submenu.h>
#include <gui/modules/text_input.h>
#include <gui/modules/text_box.h>
#include <storage/storage.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "sentinel_link.h"

#define TAG "SentinelBridge"
#define CONFIG_DIR "/ext/apps_data/sentinel_flipper"
#define CONFIG_PATH CONFIG_DIR "/config.txt"
#define TRACE_PATH CONFIG_DIR "/trace.log"

#define FIELD_LEN_SSID 33
#define FIELD_LEN_PASS 65
#define FIELD_LEN_URL  96
#define FIELD_LEN_CODE 16

typedef enum {
    SentinelViewSubmenu,
    SentinelViewTextInput,
    SentinelViewStatus,
} SentinelView;

typedef enum {
    SentinelEventLine = 1,
    SentinelEventBack,
} SentinelEvent;

typedef enum {
    MenuLinkMode,
    MenuWifiSsid,
    MenuWifiPass,
    MenuServerUrl,
    MenuPairCode,
    MenuConnect,
    MenuPair,
    MenuLock,
    MenuSecure,
    MenuAway,
    MenuUnlock,
    MenuShot,
    MenuRefresh,
    MenuForget,
    MenuStatus,
    MenuExit,
} MenuIndex;

typedef struct {
    char ssid[FIELD_LEN_SSID];
    char pass[FIELD_LEN_PASS];
    char url[FIELD_LEN_URL];
    char code[FIELD_LEN_CODE];
    bool bluetooth;
} SentinelConfig;

typedef struct {
    Gui* gui;
    ViewDispatcher* view_dispatcher;
    Submenu* submenu;
    TextInput* text_input;
    TextBox* status_box;

    FuriString* screen;
    FuriString* log;
    FuriString* pending;
    FuriMutex* mutex;

    SentinelConfig config;
    SentinelLink* link;
    SentinelView view;
    uint32_t tick;

    char* edit_target;
    size_t edit_target_len;
    char edit_buffer[FIELD_LEN_URL];

    bool have_state;
    bool online;
    bool away;
    bool secure;
    char name[FIELD_LEN_SSID];
    char session[16];
    char current_code[8];
    int seconds_left;
} SentinelApp;

// ---- trace --------------------------------------------------------

static void sentinel_trace(const char* line) {
    Storage* storage = furi_record_open(RECORD_STORAGE);
    File* file = storage_file_alloc(storage);
    FileInfo info;
    if(storage_common_stat(storage, TRACE_PATH, &info) == FSE_OK && info.size > 8192) {
        storage_common_remove(storage, TRACE_PATH);
    }
    if(storage_file_open(file, TRACE_PATH, FSAM_WRITE, FSOM_OPEN_APPEND)) {
        storage_file_write(file, line, strlen(line));
        storage_file_write(file, "\n", 1);
    }
    storage_file_close(file);
    storage_file_free(file);
    furi_record_close(RECORD_STORAGE);
}

static void sentinel_switch(SentinelApp* app, SentinelView view) {
    char trace[24];
    snprintf(trace, sizeof(trace), "view %d", (int)view);
    sentinel_trace(trace);
    app->view = view;
    view_dispatcher_switch_to_view(app->view_dispatcher, view);
}

// ---- persistence --------------------------------------------------

static void sentinel_config_save(SentinelApp* app) {
    Storage* storage = furi_record_open(RECORD_STORAGE);
    storage_common_mkdir(storage, CONFIG_DIR);
    File* file = storage_file_alloc(storage);
    if(storage_file_open(file, CONFIG_PATH, FSAM_WRITE, FSOM_CREATE_ALWAYS)) {
        FuriString* line = furi_string_alloc_printf(
            "ssid=%s\npass=%s\nurl=%s\ncode=%s\nlink=%s\n",
            app->config.ssid,
            app->config.pass,
            app->config.url,
            app->config.code,
            app->config.bluetooth ? "ble" : "uart");
        storage_file_write(file, furi_string_get_cstr(line), furi_string_size(line));
        furi_string_free(line);
    }
    storage_file_close(file);
    storage_file_free(file);
    furi_record_close(RECORD_STORAGE);
}

static void sentinel_config_load(SentinelApp* app) {
    Storage* storage = furi_record_open(RECORD_STORAGE);
    File* file = storage_file_alloc(storage);
    if(storage_file_open(file, CONFIG_PATH, FSAM_READ, FSOM_OPEN_EXISTING)) {
        char buf[384];
        char link[8] = {0};
        size_t read = storage_file_read(file, buf, sizeof(buf) - 1);
        buf[read] = '\0';
        sscanf(
            buf,
            "ssid=%32[^\n]\npass=%64[^\n]\nurl=%95[^\n]\ncode=%15[^\n]\nlink=%7[^\n]",
            app->config.ssid,
            app->config.pass,
            app->config.url,
            app->config.code,
            link);
        app->config.bluetooth = strcmp(link, "uart") != 0;
    }
    storage_file_close(file);
    storage_file_free(file);
    furi_record_close(RECORD_STORAGE);

    if(app->config.url[0] == '\0') {
        strncpy(app->config.url, "https://sentinel.whatavibe.net", sizeof(app->config.url) - 1);
    }
}

// ---- status rendering ---------------------------------------------

static void sentinel_render(SentinelApp* app) {
    furi_string_reset(app->screen);
    if(app->have_state) {
        furi_string_cat_printf(app->screen, "%s\n", app->name[0] ? app->name : "PC");
        furi_string_cat_printf(app->screen, "%s", app->online ? "online" : "offline");
        if(app->away) furi_string_cat_printf(app->screen, " · away");
        if(app->secure) furi_string_cat_printf(app->screen, " · secure lock");
        if(app->session[0]) furi_string_cat_printf(app->screen, " · %s", app->session);
        furi_string_cat_printf(app->screen, "\n");

        if(strlen(app->current_code) == 6 && app->seconds_left > 0) {
            furi_string_cat_printf(
                app->screen,
                "Code %.3s %.3s · %ds\n",
                app->current_code,
                app->current_code + 3,
                app->seconds_left);
        } else {
            furi_string_cat_printf(app->screen, "Code ------ · --\n");
        }
    } else {
        furi_string_cat_printf(
            app->screen,
            "Sentinel\nWaiting for %s...\n",
            app->config.bluetooth ? "the phone" : "the WiFi board");
    }

    if(furi_string_size(app->log) > 0) {
        furi_string_cat_printf(app->screen, "\n%s", furi_string_get_cstr(app->log));
    }
    text_box_set_text(app->status_box, furi_string_get_cstr(app->screen));
}

static void sentinel_log(SentinelApp* app, const char* line) {
    if(furi_string_size(app->log) > 500) furi_string_reset(app->log);
    furi_string_cat_printf(app->log, "%s\n", line);
}

static void sentinel_parse_state(SentinelApp* app, const char* line) {
    char buf[160];
    strncpy(buf, line, sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = '\0';

    char* fields[8] = {0};
    int count = 0;
    fields[count++] = buf;
    for(char* p = buf; *p && count < 8; p++) {
        if(*p == '|') {
            *p = '\0';
            fields[count++] = p + 1;
        }
    }
    if(count < 7) return;

    app->have_state = true;
    app->online = atoi(fields[1]) != 0;
    app->away = atoi(fields[2]) != 0;
    app->secure = atoi(fields[3]) != 0;
    app->seconds_left = atoi(fields[6]);
    strncpy(app->session, fields[4], sizeof(app->session) - 1);
    app->session[sizeof(app->session) - 1] = '\0';
    strncpy(app->current_code, fields[5], sizeof(app->current_code) - 1);
    app->current_code[sizeof(app->current_code) - 1] = '\0';
    if(count >= 8 && fields[7][0]) {
        strncpy(app->name, fields[7], sizeof(app->name) - 1);
        app->name[sizeof(app->name) - 1] = '\0';
    } else {
        app->name[0] = '\0';
    }
}

static void sentinel_handle_line(SentinelApp* app, const char* line) {
    if(strcmp(line, "PING") == 0) {
        sentinel_link_send(app->link, "PONG");
        return;
    }
    if(strcmp(line, "PONG") == 0) return;
    if(strncmp(line, "STATE|", 6) != 0) {
        // STATE arrives every few seconds; only trace the rare lines.
        char trace[48];
        snprintf(trace, sizeof(trace), "rx %.24s", line);
        sentinel_trace(trace);
    }
    if(strncmp(line, "STATE|", 6) == 0) {
        sentinel_parse_state(app, line);
    } else if(strncmp(line, "INFO|", 5) == 0) {
        sentinel_log(app, line + 5);
    } else if(strncmp(line, "ERR|", 4) == 0) {
        furi_string_cat_printf(app->log, "! %s\n", line + 4);
        if(furi_string_size(app->log) > 700) furi_string_reset(app->log);
    } else {
        sentinel_log(app, line);
    }
}

// Called from the UART worker thread: only queue bytes, never touch the GUI.
static void sentinel_rx_callback(const char* line, void* context) {
    SentinelApp* app = context;
    furi_mutex_acquire(app->mutex, FuriWaitForever);
    if(furi_string_size(app->pending) < 2048) {
        furi_string_cat_printf(app->pending, "%s\n", line);
    }
    furi_mutex_release(app->mutex);
    view_dispatcher_send_custom_event(app->view_dispatcher, SentinelEventLine);
}

// Runs on the GUI thread, so text_box_set_text and friends are safe.
static bool sentinel_custom_event(void* context, uint32_t event) {
    SentinelApp* app = context;

    if(event == SentinelEventBack) {
        if(app->view == SentinelViewSubmenu) {
            view_dispatcher_stop(app->view_dispatcher);
        } else {
            sentinel_trace("back -> submenu");
            sentinel_switch(app, SentinelViewSubmenu);
        }
        return true;
    }
    if(event != SentinelEventLine) return false;

    furi_mutex_acquire(app->mutex, FuriWaitForever);
    FuriString* chunk = furi_string_alloc_set(app->pending);
    furi_string_reset(app->pending);
    furi_mutex_release(app->mutex);

    const char* text = furi_string_get_cstr(chunk);
    size_t start = 0;
    for(size_t i = 0;; i++) {
        if(text[i] == '\n' || text[i] == '\0') {
            if(i > start) {
                char line[192];
                size_t length = i - start;
                if(length > sizeof(line) - 1) length = sizeof(line) - 1;
                memcpy(line, text + start, length);
                line[length] = '\0';
                sentinel_handle_line(app, line);
            }
            start = i + 1;
            if(text[i] == '\0') break;
        }
    }
    furi_string_free(chunk);
    sentinel_render(app);
    return true;
}

// ---- menu ---------------------------------------------------------

static void sentinel_build_menu(SentinelApp* app);
static void sentinel_start_text_input(
    SentinelApp* app,
    const char* header,
    char* target,
    size_t target_len,
    bool is_password);

static void sentinel_send_config(SentinelApp* app) {
    FuriString* cmd = furi_string_alloc_printf("SSID:%s", app->config.ssid);
    sentinel_link_send(app->link, furi_string_get_cstr(cmd));
    furi_string_printf(cmd, "PASS:%s", app->config.pass);
    sentinel_link_send(app->link, furi_string_get_cstr(cmd));
    furi_string_printf(cmd, "URL:%s", app->config.url);
    sentinel_link_send(app->link, furi_string_get_cstr(cmd));
    furi_string_free(cmd);
}

static void sentinel_action(SentinelApp* app, const char* command, const char* label) {
    furi_string_reset(app->log);
    furi_string_cat_printf(app->log, "%s…\n", label);
    bool ok = sentinel_link_send(app->link, command);
    char trace[48];
    snprintf(trace, sizeof(trace), "tx %s ok=%d", command, ok ? 1 : 0);
    sentinel_trace(trace);
    sentinel_render(app);
    sentinel_switch(app, SentinelViewStatus);
}

static void sentinel_text_input_done(void* context) {
    SentinelApp* app = context;
    strncpy(app->edit_target, app->edit_buffer, app->edit_target_len - 1);
    app->edit_target[app->edit_target_len - 1] = '\0';
    sentinel_config_save(app);
    sentinel_switch(app, SentinelViewSubmenu);
}

static void sentinel_start_text_input(
    SentinelApp* app,
    const char* header,
    char* target,
    size_t target_len,
    bool is_password) {
    app->edit_target = target;
    app->edit_target_len = target_len;
    memset(app->edit_buffer, 0, sizeof(app->edit_buffer));
    if(target) strncpy(app->edit_buffer, target, sizeof(app->edit_buffer) - 1);

    text_input_set_header_text(app->text_input, header);
    size_t max_len = app->edit_target_len < sizeof(app->edit_buffer)
                         ? app->edit_target_len
                         : sizeof(app->edit_buffer);
    text_input_set_result_callback(
        app->text_input, sentinel_text_input_done, app, app->edit_buffer, max_len, false);
    UNUSED(is_password);
    sentinel_switch(app, SentinelViewTextInput);
}

static void sentinel_submenu_callback(void* context, uint32_t index) {
    SentinelApp* app = context;
    char trace[48];
    snprintf(trace, sizeof(trace), "menu %lu", (unsigned long)index);
    sentinel_trace(trace);
    switch(index) {
    case MenuLinkMode:
        app->config.bluetooth = !app->config.bluetooth;
        sentinel_config_save(app);
        sentinel_link_set_mode(
            app->link, app->config.bluetooth ? SentinelLinkModeBle : SentinelLinkModeUart);
        furi_string_reset(app->log);
        sentinel_log(
            app,
            app->config.bluetooth
                ? "Bluetooth mode - connect from the Sentinel phone app."
                : "WiFi board mode - set WiFi, relay URL and pair code.");
        sentinel_build_menu(app);
        sentinel_switch(app, SentinelViewSubmenu);
        break;
    case MenuWifiSsid:
        sentinel_start_text_input(app, "WiFi SSID (2.4 GHz)", app->config.ssid, FIELD_LEN_SSID, false);
        break;
    case MenuWifiPass:
        sentinel_start_text_input(app, "WiFi password", app->config.pass, FIELD_LEN_PASS, true);
        break;
    case MenuServerUrl:
        sentinel_start_text_input(
            app, "Relay URL (https://…)", app->config.url, FIELD_LEN_URL, false);
        break;
    case MenuPairCode:
        sentinel_start_text_input(
            app, "Pairing code (PC → Pair another phone)", app->config.code, FIELD_LEN_CODE, false);
        break;
    case MenuConnect:
        furi_string_reset(app->log);
        if(app->config.bluetooth) {
            furi_string_cat_printf(app->log, "Requesting status...\n");
            sentinel_link_send(app->link, "STATUS");
        } else {
            sentinel_send_config(app);
            sentinel_link_send(app->link, "CONNECT");
        }
        sentinel_render(app);
        sentinel_switch(app, SentinelViewStatus);
        break;
    case MenuPair: {
        if(app->config.bluetooth) break;
        furi_string_reset(app->log);
        sentinel_send_config(app);
        FuriString* cmd = furi_string_alloc_printf("PAIR:%s", app->config.code);
        sentinel_link_send(app->link, furi_string_get_cstr(cmd));
        furi_string_free(cmd);
        sentinel_render(app);
        sentinel_switch(app, SentinelViewStatus);
        break;
    }
    case MenuLock:
        sentinel_action(app, "LOCK", "Locking PC");
        break;
    case MenuSecure:
        sentinel_action(app, "SECURE", "Engaging secure lock");
        break;
    case MenuAway:
        sentinel_action(app, "AWAY", "Toggling away mode");
        break;
    case MenuUnlock:
        sentinel_action(app, "UNLOCK", "Unlocking");
        break;
    case MenuShot:
        sentinel_action(app, "SHOT", "Taking screenshot");
        break;
    case MenuRefresh:
        sentinel_action(app, "STATUS", "Refreshing");
        break;
    case MenuForget:
        if(app->config.bluetooth) break;
        furi_string_reset(app->log);
        sentinel_link_send(app->link, "FORGET");
        sentinel_render(app);
        sentinel_switch(app, SentinelViewStatus);
        break;
    case MenuStatus:
        sentinel_render(app);
        sentinel_switch(app, SentinelViewStatus);
        break;
    case MenuExit:
        view_dispatcher_stop(app->view_dispatcher);
        break;
    }
}

static void sentinel_build_menu(SentinelApp* app) {
    submenu_reset(app->submenu);
    submenu_set_header(app->submenu, "Sentinel Bridge");
    submenu_add_item(
        app->submenu,
        app->config.bluetooth ? "Link: Bluetooth" : "Link: WiFi board",
        MenuLinkMode,
        sentinel_submenu_callback,
        app);

    if(!app->config.bluetooth) {
        submenu_add_item(app->submenu, "WiFi SSID", MenuWifiSsid, sentinel_submenu_callback, app);
        submenu_add_item(app->submenu, "WiFi Password", MenuWifiPass, sentinel_submenu_callback, app);
        submenu_add_item(app->submenu, "Relay URL", MenuServerUrl, sentinel_submenu_callback, app);
        submenu_add_item(app->submenu, "Pairing Code", MenuPairCode, sentinel_submenu_callback, app);
        submenu_add_item(app->submenu, "Pair / Re-pair", MenuPair, sentinel_submenu_callback, app);
    }

    submenu_add_item(app->submenu, "Connect", MenuConnect, sentinel_submenu_callback, app);
    submenu_add_item(app->submenu, "Lock PC", MenuLock, sentinel_submenu_callback, app);
    submenu_add_item(app->submenu, "Secure Lock", MenuSecure, sentinel_submenu_callback, app);
    submenu_add_item(app->submenu, "Away On/Off", MenuAway, sentinel_submenu_callback, app);
    submenu_add_item(app->submenu, "Unlock PC", MenuUnlock, sentinel_submenu_callback, app);
    submenu_add_item(app->submenu, "Screenshot", MenuShot, sentinel_submenu_callback, app);
    submenu_add_item(app->submenu, "Refresh Status", MenuRefresh, sentinel_submenu_callback, app);
    if(!app->config.bluetooth) {
        submenu_add_item(app->submenu, "Forget Pairing", MenuForget, sentinel_submenu_callback, app);
    }
    submenu_add_item(app->submenu, "Status Log", MenuStatus, sentinel_submenu_callback, app);
    submenu_add_item(app->submenu, "Exit", MenuExit, sentinel_submenu_callback, app);
}

static bool sentinel_navigation_event(void* context) {
    if(!context) return false;
    SentinelApp* app = context;
    // Never touch the view tree from inside the input callback: queue it instead.
    view_dispatcher_send_custom_event(app->view_dispatcher, SentinelEventBack);
    return true;
}

// Keeps the BLE link warm and gives the phone a liveness signal.
static void sentinel_tick(void* context) {
    SentinelApp* app = context;
    if(!sentinel_link_ble_connected(app->link)) return;
    app->tick++;
    if(app->tick % 10 == 0) sentinel_link_ping(app->link);
}

// ---- lifecycle ----------------------------------------------------

static SentinelApp* sentinel_app_alloc(void) {
    Storage* storage = furi_record_open(RECORD_STORAGE);
    storage_common_remove(storage, TRACE_PATH);
    furi_record_close(RECORD_STORAGE);
    sentinel_trace("alloc");
    SentinelApp* app = malloc(sizeof(SentinelApp));
    memset(app, 0, sizeof(SentinelApp));
    app->config.bluetooth = true;

    app->screen = furi_string_alloc();
    app->log = furi_string_alloc();
    sentinel_config_load(app);

    app->gui = furi_record_open(RECORD_GUI);
    app->view_dispatcher = view_dispatcher_alloc();
    app->mutex = furi_mutex_alloc(FuriMutexTypeNormal);
    app->pending = furi_string_alloc();
    view_dispatcher_set_event_callback_context(app->view_dispatcher, app);
    view_dispatcher_set_custom_event_callback(app->view_dispatcher, sentinel_custom_event);
    view_dispatcher_set_navigation_event_callback(app->view_dispatcher, sentinel_navigation_event);
    view_dispatcher_set_tick_event_callback(app->view_dispatcher, sentinel_tick, 1000);
    view_dispatcher_attach_to_gui(app->view_dispatcher, app->gui, ViewDispatcherTypeFullscreen);

    app->submenu = submenu_alloc();
    sentinel_build_menu(app);
    view_dispatcher_add_view(
        app->view_dispatcher, SentinelViewSubmenu, submenu_get_view(app->submenu));

    app->text_input = text_input_alloc();
    View* text_input_view = text_input_get_view(app->text_input);
    view_dispatcher_add_view(app->view_dispatcher, SentinelViewTextInput, text_input_view);

    app->status_box = text_box_alloc();
    View* status_view = text_box_get_view(app->status_box);
    view_dispatcher_add_view(app->view_dispatcher, SentinelViewStatus, status_view);

    app->link = sentinel_link_alloc(
        sentinel_rx_callback,
        app,
        app->config.bluetooth ? SentinelLinkModeBle : SentinelLinkModeUart);
    sentinel_log(
        app,
        app->config.bluetooth ? "Connect from the Sentinel phone app." :
                                "Set WiFi, relay URL and pair code, then Connect.");
    sentinel_render(app);

    sentinel_switch(app, SentinelViewSubmenu);
    return app;
}

static void sentinel_app_free(SentinelApp* app) {
    sentinel_trace("free: link");
    sentinel_link_free(app->link);
    sentinel_trace("free: views");
    view_dispatcher_remove_view(app->view_dispatcher, SentinelViewStatus);
    view_dispatcher_remove_view(app->view_dispatcher, SentinelViewTextInput);
    view_dispatcher_remove_view(app->view_dispatcher, SentinelViewSubmenu);
    sentinel_trace("free: modules");
    text_box_free(app->status_box);
    text_input_free(app->text_input);
    submenu_free(app->submenu);
    view_dispatcher_free(app->view_dispatcher);
    furi_record_close(RECORD_GUI);
    furi_string_free(app->pending);
    furi_mutex_free(app->mutex);
    furi_string_free(app->log);
    furi_string_free(app->screen);
    free(app);
    sentinel_trace("free: done");
}

int32_t sentinel_app_main(void* p) {
    UNUSED(p);
    SentinelApp* app = sentinel_app_alloc();
    view_dispatcher_run(app->view_dispatcher);
    sentinel_app_free(app);
    return 0;
}
