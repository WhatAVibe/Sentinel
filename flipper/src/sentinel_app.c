/*
 * Sentinel Bridge — Flipper Zero app
 * ----------------------------------
 * UI, menu, config persistence, link-state rendering and app lifecycle.
 *
 * Views:
 *   Submenu    - main menu (link mode, WiFi/relay config, actions)
 *   TextInput  - editing a single config field
 *   Status     - a dedicated, hand-drawn panel: PC name, online/away/secure,
 *                the live TOTP code and its countdown. No scrolling text here
 *                on purpose — it's a glanceable dashboard, not a log.
 *   Log        - the raw INFO/ERR/trace feed, in a TextBox that opens at the
 *                top and stays there: new lines never yank the view down to
 *                the bottom. Scroll it yourself with Up/Down.
 *
 * Threading:
 *   The link's RX worker thread (sentinel_link.c) only ever queues bytes and
 *   posts a custom event; it never touches a View, a model or the submenu.
 *   All GUI reads/writes happen on the dispatcher's own thread, inside the
 *   custom-event callback below. Back is handled the same way: the
 *   dispatcher's navigation callback posts an event instead of switching
 *   views itself, so a view is never mutated out from under its own input
 *   handler.
 */

#include <furi.h>
#include <gui/gui.h>
#include <gui/elements.h>
#include <gui/view_dispatcher.h>
#include <gui/modules/submenu.h>
#include <gui/modules/text_input.h>
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

#define BANNER_LEN   72
#define LOG_BUF_SIZE 2048
#define LOG_LINE_COLS 20
#define LOG_PANEL_ROWS 5

typedef enum {
    SentinelViewSubmenu,
    SentinelViewTextInput,
    SentinelViewStatus,
    SentinelViewLog,
    SentinelViewCode,
} SentinelView;

typedef enum {
    SentinelEventLine = 1,
    SentinelEventBack,
    SentinelEventOpenLog,
} SentinelEvent;

typedef enum {
    MenuLinkMode,
    MenuWifiSsid,
    MenuWifiPass,
    MenuServerUrl,
    MenuPairCode,
    MenuConnect,
    MenuCode,
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

// Everything the Status panel draws. Plain fixed-size fields only — no
// pointers into a FuriString or anything else that could move or be freed
// out from under the draw callback.
typedef struct {
    bool have_state;
    bool online;
    bool away;
    bool secure;
    bool bluetooth_mode;
    char name[FIELD_LEN_SSID];
    char session[16];
    char code[8];
    int seconds_left;
    char note[BANNER_LEN];
    bool note_is_error;
} StatusModel;

typedef struct {
    char text[LOG_BUF_SIZE];
    int32_t scroll;
    bool bluetooth_mode;
    bool connected;
} LogModel;

typedef struct {
    bool have_state;
    bool bluetooth_mode;
    char code[8];
    int seconds_left;
} CodeModel;

typedef struct {
    Gui* gui;
    ViewDispatcher* view_dispatcher;
    Submenu* submenu;
    TextInput* text_input;
    View* status_view;
    View* log_view;
    View* code_view;

    FuriString* log;
    FuriString* pending;
    FuriMutex* mutex;

    // Stable backing buffer for the Log view. TextBox keeps a raw pointer to
    // whatever we hand it, so we always point it at this fixed array rather
    // than a FuriString's internal buffer, which can move on every realloc.
    char log_buf[LOG_BUF_SIZE];

    char banner[BANNER_LEN];
    bool banner_is_error;

    SentinelConfig config;
    SentinelLink* link;
    SentinelView view;
    uint32_t tick;
    volatile bool alive;

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
    if(!app) return;
    char trace[24];
    snprintf(trace, sizeof(trace), "view %d", (int)view);
    sentinel_trace(trace);
    app->view = view;
    view_dispatcher_switch_to_view(app->view_dispatcher, view);
    sentinel_trace("switched");
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

// ---- status / log rendering ----------------------------------------

static void sentinel_set_banner(SentinelApp* app, const char* text, bool is_error) {
    if(!app || !text) return;
    strncpy(app->banner, text, sizeof(app->banner) - 1);
    app->banner[sizeof(app->banner) - 1] = '\0';
    app->banner_is_error = is_error;
}

static void sentinel_log(SentinelApp* app, const char* line) {
    if(furi_string_size(app->log) > 500) furi_string_reset(app->log);
    furi_string_cat_printf(app->log, "%s\n", line);
}

// Copies the FuriString log into our own fixed buffer, pre-wrapped to the
// width of the Log panel, then pushes it into that view's model. The view
// never holds a pointer into a FuriString.
static void sentinel_log_sync(SentinelApp* app) {
    if(!app || !app->log_view) return;

    const char* src = furi_string_get_cstr(app->log);
    size_t out = 0;
    while(*src && out + LOG_LINE_COLS + 2 < sizeof(app->log_buf)) {
        const char* nl = strchr(src, '\n');
        size_t len = nl ? (size_t)(nl - src) : strlen(src);
        size_t pos = 0;
        do {
            size_t chunk = len - pos;
            if(chunk > LOG_LINE_COLS) chunk = LOG_LINE_COLS;
            if(out + chunk + 1 >= sizeof(app->log_buf)) break;
            memcpy(app->log_buf + out, src + pos, chunk);
            out += chunk;
            app->log_buf[out++] = '\n';
            pos += chunk;
        } while(pos < len);
        src = nl ? nl + 1 : src + len;
    }
    app->log_buf[out] = '\0';

    with_view_model(
        app->log_view,
        LogModel* model,
        {
            strncpy(model->text, app->log_buf, sizeof(model->text) - 1);
            model->text[sizeof(model->text) - 1] = '\0';
            model->bluetooth_mode = app->config.bluetooth;
            model->connected = app->have_state;
        },
        true);
}

static void sentinel_status_sync(SentinelApp* app) {
    if(!app || !app->status_view) return;
    with_view_model(
        app->status_view,
        StatusModel * model,
        {
            model->have_state = app->have_state;
            model->online = app->online;
            model->away = app->away;
            model->secure = app->secure;
            model->bluetooth_mode = app->config.bluetooth;
            strncpy(model->name, app->name, sizeof(model->name) - 1);
            model->name[sizeof(model->name) - 1] = '\0';
            strncpy(model->session, app->session, sizeof(model->session) - 1);
            model->session[sizeof(model->session) - 1] = '\0';
            strncpy(model->code, app->current_code, sizeof(model->code) - 1);
            model->code[sizeof(model->code) - 1] = '\0';
            model->seconds_left = app->seconds_left;
            strncpy(model->note, app->banner, sizeof(model->note) - 1);
            model->note[sizeof(model->note) - 1] = '\0';
            model->note_is_error = app->banner_is_error;
        },
        true);
}

static void sentinel_code_sync(SentinelApp* app) {
    if(!app || !app->code_view) return;
    with_view_model(
        app->code_view,
        CodeModel * model,
        {
            model->have_state = app->have_state;
            model->bluetooth_mode = app->config.bluetooth;
            model->seconds_left = app->seconds_left;
            strncpy(model->code, app->current_code, sizeof(model->code) - 1);
            model->code[sizeof(model->code) - 1] = '\0';
        },
        true);
}

// Call after anything changes: keeps every panel current regardless of which
// one is on screen right now.
static void sentinel_refresh(SentinelApp* app) {
    if(!app) return;
    sentinel_status_sync(app);
    sentinel_log_sync(app);
    sentinel_code_sync(app);
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
        sentinel_set_banner(app, line + 5, false);
    } else if(strncmp(line, "ERR|", 4) == 0) {
        furi_string_cat_printf(app->log, "! %s\n", line + 4);
        if(furi_string_size(app->log) > 700) furi_string_reset(app->log);
        sentinel_set_banner(app, line + 4, true);
    } else {
        sentinel_log(app, line);
    }
}

// Called from the link's RX worker thread: only queue bytes, never touch
// the GUI, a View or a model from here.
static void sentinel_rx_callback(const char* line, void* context) {
    SentinelApp* app = context;
    if(!app || !app->alive) return;
    furi_mutex_acquire(app->mutex, FuriWaitForever);
    if(furi_string_size(app->pending) < 2048) {
        furi_string_cat_printf(app->pending, "%s\n", line);
    }
    furi_mutex_release(app->mutex);
    view_dispatcher_send_custom_event(app->view_dispatcher, SentinelEventLine);
}

// Runs on the GUI thread, so touching views/models here is safe.
static bool sentinel_custom_event(void* context, uint32_t event) {
    if(!context) return false;
    SentinelApp* app = context;
    if(!app->alive) return false;

    if(event == SentinelEventBack) {
        sentinel_trace("back");
        if(app->view == SentinelViewSubmenu) {
            sentinel_trace("back: exit");
            view_dispatcher_stop(app->view_dispatcher);
        } else {
            sentinel_trace("back: menu");
            sentinel_switch(app, SentinelViewSubmenu);
            sentinel_trace("back: done");
        }
        return true;
    }

    if(event == SentinelEventOpenLog) {
        sentinel_switch(app, SentinelViewLog);
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
    sentinel_refresh(app);
    return true;
}

// ---- status panel ---------------------------------------------------

static void sentinel_status_draw(Canvas* canvas, void* model) {
    StatusModel* m = model;
    canvas_clear(canvas);

    canvas_set_color(canvas, ColorBlack);
    canvas_draw_box(canvas, 0, 0, 128, 13);
    canvas_set_color(canvas, ColorWhite);
    canvas_set_font(canvas, FontPrimary);
    canvas_draw_str(canvas, 4, 10, m->bluetooth_mode ? "Sentinel - Bluetooth" : "Sentinel - WiFi board");
    canvas_set_color(canvas, ColorBlack);

    if(!m->have_state) {
        canvas_set_font(canvas, FontSecondary);
        canvas_draw_str(canvas, 4, 29, "Waiting for link...");
        if(m->note[0]) {
            char line[28];
            strncpy(line, m->note, sizeof(line) - 1);
            line[sizeof(line) - 1] = '\0';
            canvas_draw_str(canvas, 4, 41, line);
        }
        canvas_draw_str(canvas, 4, 60, "OK: log   BACK: menu");
        return;
    }

    canvas_set_font(canvas, FontPrimary);
    canvas_draw_str(canvas, 4, 25, m->name[0] ? m->name : "PC");

    canvas_set_font(canvas, FontSecondary);
    {
        char line[48];
        int n = snprintf(line, sizeof(line), "%s", m->online ? "online" : "offline");
        if(m->away && n > 0 && (size_t)n < sizeof(line))
            n += snprintf(line + n, sizeof(line) - n, " . away");
        if(m->secure && n > 0 && (size_t)n < sizeof(line))
            n += snprintf(line + n, sizeof(line) - n, " . locked");
        if(m->session[0] && n > 0 && (size_t)n < sizeof(line))
            snprintf(line + n, sizeof(line) - n, " . %s", m->session);
        canvas_draw_str(canvas, 4, 35, line);
    }

    canvas_draw_line(canvas, 0, 39, 128, 39);

    if(strlen(m->code) == 6 && m->seconds_left > 0) {
        char code_buf[8];
        snprintf(code_buf, sizeof(code_buf), "%.3s %.3s", m->code, m->code + 3);
        canvas_set_font(canvas, FontBigNumbers);
        canvas_draw_str(canvas, 10, 56, code_buf);

        float progress = (float)m->seconds_left / 30.0f;
        if(progress > 1.0f) progress = 1.0f;
        if(progress < 0.0f) progress = 0.0f;
        elements_progress_bar(canvas, 4, 59, 120, progress);
    } else {
        canvas_set_font(canvas, FontSecondary);
        canvas_draw_str(canvas, 4, 50, "Code ------");
        if(m->note[0]) {
            char line[28];
            strncpy(line, m->note, sizeof(line) - 1);
            line[sizeof(line) - 1] = '\0';
            canvas_draw_str(canvas, 4, 62, line);
        }
    }
}

static bool sentinel_status_input(InputEvent* event, void* context) {
    SentinelApp* app = context;
    if(!app) return false;
    if(event->type == InputTypeShort && event->key == InputKeyOk) {
        view_dispatcher_send_custom_event(app->view_dispatcher, SentinelEventOpenLog);
        return true;
    }
    return false;
}

// ---- log panel ------------------------------------------------------

static void sentinel_log_draw(Canvas* canvas, void* model) {
    LogModel* m = model;
    canvas_clear(canvas);

    canvas_set_color(canvas, ColorBlack);
    canvas_draw_box(canvas, 0, 0, 128, 12);
    canvas_set_color(canvas, ColorWhite);
    canvas_set_font(canvas, FontSecondary);
    canvas_draw_str(canvas, 3, 9, m->bluetooth_mode ? "Log - Bluetooth" : "Log - WiFi board");
    canvas_set_color(canvas, ColorBlack);

    int32_t rows = 0;
    for(const char* p = m->text; *p; p++) {
        if(*p == '\n') rows++;
    }
    int32_t max_scroll = rows > LOG_PANEL_ROWS ? rows - LOG_PANEL_ROWS : 0;
    int32_t scroll = m->scroll;
    if(scroll > max_scroll) scroll = max_scroll;
    if(scroll < 0) scroll = 0;

    const char* p = m->text;
    int32_t row = 0;
    int32_t y = 22;
    while(*p) {
        if(row >= scroll && row < scroll + LOG_PANEL_ROWS) {
            char line[LOG_LINE_COLS + 1];
            size_t i = 0;
            while(*p && *p != '\n' && i < LOG_LINE_COLS) line[i++] = *p++;
            line[i] = '\0';
            canvas_draw_str(canvas, 3, y, line);
            y += 10;
            while(*p && *p != '\n') p++;
        } else {
            while(*p && *p != '\n') p++;
        }
        if(*p == '\n') p++;
        row++;
    }

    if(rows > LOG_PANEL_ROWS) {
        elements_scrollbar(canvas, scroll, max_scroll);
    }
}

static bool sentinel_log_input(InputEvent* event, void* context) {
    SentinelApp* app = context;
    if(!app || !app->log_view) return false;
    if(event->type != InputTypeShort && event->type != InputTypeRepeat) return false;
    if(event->key == InputKeyUp) {
        with_view_model(
            app->log_view, LogModel * model, { if(model->scroll > 0) model->scroll--; }, true);
        return true;
    }
    if(event->key == InputKeyDown) {
        with_view_model(app->log_view, LogModel * model, { model->scroll++; }, true);
        return true;
    }
    return false;
}

// ---- 2FA panel ------------------------------------------------------

static void sentinel_code_draw(Canvas* canvas, void* model) {
    CodeModel* m = model;
    canvas_clear(canvas);

    canvas_set_color(canvas, ColorBlack);
    canvas_draw_box(canvas, 0, 0, 128, 12);
    canvas_set_color(canvas, ColorWhite);
    canvas_set_font(canvas, FontSecondary);
    canvas_draw_str(canvas, 3, 9, m->bluetooth_mode ? "2FA - Bluetooth" : "2FA - WiFi board");
    canvas_set_color(canvas, ColorBlack);

    if(!m->have_state || strlen(m->code) != 6) {
        canvas_set_font(canvas, FontSecondary);
        canvas_draw_str(canvas, 3, 34, "Waiting for a code...");
        canvas_draw_str(canvas, 3, 60, "BACK: menu");
        return;
    }

    char code_buf[10];
    snprintf(code_buf, sizeof(code_buf), "%.3s %.3s", m->code, m->code + 3);
    canvas_set_font(canvas, FontBigNumbers);
    canvas_draw_str(canvas, 10, 34, code_buf);

    float progress = (float)m->seconds_left / 30.0f;
    if(progress > 1.0f) progress = 1.0f;
    if(progress < 0.0f) progress = 0.0f;
    elements_progress_bar(canvas, 4, 42, 120, progress);

    canvas_set_font(canvas, FontPrimary);
    char sec_buf[12];
    snprintf(sec_buf, sizeof(sec_buf), "%ds", m->seconds_left);
    uint16_t width = canvas_string_width(canvas, sec_buf);
    canvas_draw_str(canvas, (128 - width) / 2, 62, sec_buf);
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
    sentinel_set_banner(app, label, false);
    bool ok = sentinel_link_send(app->link, command);
    char trace[48];
    snprintf(trace, sizeof(trace), "tx %s ok=%d", command, ok ? 1 : 0);
    sentinel_trace(trace);
    sentinel_refresh(app);
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
        sentinel_set_banner(
            app,
            app->config.bluetooth
                ? "Bluetooth mode - connect from the Sentinel phone app."
                : "WiFi board mode - set WiFi, relay URL and pair code.",
            false);
        sentinel_log(app, app->banner);
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
            sentinel_set_banner(app, "Requesting status...", false);
            sentinel_link_send(app->link, "STATUS");
        } else {
            sentinel_set_banner(app, "Connecting...", false);
            sentinel_send_config(app);
            sentinel_link_send(app->link, "CONNECT");
        }
        sentinel_log(app, app->banner);
        sentinel_refresh(app);
        sentinel_switch(app, SentinelViewStatus);
        break;
    case MenuCode:
        sentinel_code_sync(app);
        sentinel_switch(app, SentinelViewCode);
        break;
    case MenuPair: {
        if(app->config.bluetooth) break;
        furi_string_reset(app->log);
        sentinel_set_banner(app, "Pairing...", false);
        sentinel_send_config(app);
        FuriString* cmd = furi_string_alloc_printf("PAIR:%s", app->config.code);
        sentinel_link_send(app->link, furi_string_get_cstr(cmd));
        furi_string_free(cmd);
        sentinel_refresh(app);
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
        sentinel_refresh(app);
        sentinel_switch(app, SentinelViewStatus);
        break;
    case MenuStatus:
        sentinel_refresh(app);
        sentinel_switch(app, SentinelViewLog);
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
    submenu_add_item(app->submenu, "2FA Code", MenuCode, sentinel_submenu_callback, app);
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
    if(!app->alive) return false;
    // Never touch the view tree from inside the input callback: queue it instead.
    view_dispatcher_send_custom_event(app->view_dispatcher, SentinelEventBack);
    return true;
}

// Keeps the BLE link warm, counts the 2FA interval down once a second and
// gives the phone a liveness signal.
static void sentinel_tick(void* context) {
    SentinelApp* app = context;
    if(!app || !app->alive || !app->link) return;

    if(app->have_state && app->seconds_left > 0) {
        app->seconds_left--;
        if(app->view == SentinelViewCode) {
            sentinel_code_sync(app);
        } else if(app->view == SentinelViewStatus) {
            sentinel_status_sync(app);
        }
    }

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
    app->alive = true;

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

    app->status_view = view_alloc();
    view_set_context(app->status_view, app);
    view_allocate_model(app->status_view, ViewModelTypeLocking, sizeof(StatusModel));
    view_set_draw_callback(app->status_view, sentinel_status_draw);
    view_set_input_callback(app->status_view, sentinel_status_input);
    view_dispatcher_add_view(app->view_dispatcher, SentinelViewStatus, app->status_view);

    app->log_view = view_alloc();
    view_set_context(app->log_view, app);
    view_allocate_model(app->log_view, ViewModelTypeLocking, sizeof(LogModel));
    view_set_draw_callback(app->log_view, sentinel_log_draw);
    view_set_input_callback(app->log_view, sentinel_log_input);
    view_dispatcher_add_view(app->view_dispatcher, SentinelViewLog, app->log_view);

    app->code_view = view_alloc();
    view_set_context(app->code_view, app);
    view_allocate_model(app->code_view, ViewModelTypeLocking, sizeof(CodeModel));
    view_set_draw_callback(app->code_view, sentinel_code_draw);
    view_dispatcher_add_view(app->view_dispatcher, SentinelViewCode, app->code_view);

    app->link = sentinel_link_alloc(
        sentinel_rx_callback,
        app,
        app->config.bluetooth ? SentinelLinkModeBle : SentinelLinkModeUart);
    sentinel_set_banner(
        app,
        app->config.bluetooth ? "Connect from the Sentinel phone app." :
                                "Set WiFi, relay URL and pair code, then Connect.",
        false);
    sentinel_log(app, app->banner);
    sentinel_refresh(app);

    sentinel_switch(app, SentinelViewSubmenu);
    return app;
}

static void sentinel_app_free(SentinelApp* app) {
    // Stop accepting more GUI-bound events before we start tearing anything
    // down; the link is fully stopped (and its RX thread joined) right
    // after, so nothing can reach a half-freed view_dispatcher.
    app->alive = false;

    sentinel_trace("free: link");
    sentinel_link_free(app->link);
    sentinel_trace("free: views");
    view_dispatcher_remove_view(app->view_dispatcher, SentinelViewCode);
    view_dispatcher_remove_view(app->view_dispatcher, SentinelViewLog);
    view_dispatcher_remove_view(app->view_dispatcher, SentinelViewStatus);
    view_dispatcher_remove_view(app->view_dispatcher, SentinelViewTextInput);
    view_dispatcher_remove_view(app->view_dispatcher, SentinelViewSubmenu);
    sentinel_trace("free: modules");
    view_free(app->code_view);
    view_free(app->log_view);
    view_free(app->status_view);
    text_input_free(app->text_input);
    submenu_free(app->submenu);
    view_dispatcher_free(app->view_dispatcher);
    furi_record_close(RECORD_GUI);
    furi_string_free(app->pending);
    furi_mutex_free(app->mutex);
    furi_string_free(app->log);
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
