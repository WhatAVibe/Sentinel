use std::time::{Duration, Instant};

use anyhow::Result;
use eframe::egui::{self, Color32, RichText, Stroke};

use crate::agent::AgentClient;
use crate::config::{self, UsbKeyConfig};
use crate::drives::{self, DriveInfo};
use crate::ops::{self, DriveState};
use crate::startup;
use crate::state::GuardState;

const BG: Color32 = Color32::from_rgb(11, 13, 17);
const SURFACE: Color32 = Color32::from_rgb(18, 22, 28);
const ELEVATED: Color32 = Color32::from_rgb(23, 28, 36);
const INPUT: Color32 = Color32::from_rgb(14, 18, 24);
const BORDER: Color32 = Color32::from_rgb(35, 42, 53);
const BORDER_HOVER: Color32 = Color32::from_rgb(58, 69, 96);
const ACCENT: Color32 = Color32::from_rgb(108, 140, 255);
const ACCENT_DIM: Color32 = Color32::from_rgb(42, 53, 96);
const TEXT: Color32 = Color32::from_rgb(230, 233, 239);
const MUTED: Color32 = Color32::from_rgb(139, 147, 161);
const SUCCESS: Color32 = Color32::from_rgb(110, 231, 168);
const WARNING: Color32 = Color32::from_rgb(242, 196, 109);
const DANGER: Color32 = Color32::from_rgb(255, 107, 107);
const DANGER_BG: Color32 = Color32::from_rgb(58, 29, 34);
const SUCCESS_BG: Color32 = Color32::from_rgb(16, 37, 28);
const SUCCESS_BORDER: Color32 = Color32::from_rgb(30, 68, 52);
const WARNING_BG: Color32 = Color32::from_rgb(36, 30, 18);
const WARNING_BORDER: Color32 = Color32::from_rgb(74, 58, 30);

pub fn run() -> Result<()> {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([980.0, 760.0])
            .with_min_inner_size([780.0, 560.0])
            .with_title("Sentinel Key"),
        ..Default::default()
    };
    eframe::run_native(
        "Sentinel Key",
        options,
        Box::new(|cc| {
            install_style(&cc.egui_ctx);
            Ok(Box::new(KeyApp::new(&cc.egui_ctx)))
        }),
    )
    .map_err(|error| anyhow::anyhow!("{error}"))
}

struct KeyApp {
    config: UsbKeyConfig,
    key: Option<[u8; 32]>,
    drives: Vec<DriveInfo>,
    selected: Option<char>,
    guard: GuardState,
    agent_ok: bool,
    last_refresh: Instant,
    message: String,
    message_error: bool,
    confirm_forget: bool,
    confirm_enroll: bool,
    error: String,
    logo: Option<egui::TextureHandle>,
}

impl KeyApp {
    fn new(ctx: &egui::Context) -> Self {
        let config = UsbKeyConfig::load_or_create().unwrap_or_default();
        let logo = load_logo(ctx);
        let mut app = Self {
            config,
            key: None,
            drives: Vec::new(),
            selected: None,
            guard: GuardState::load(),
            agent_ok: false,
            last_refresh: Instant::now() - Duration::from_secs(10),
            message: String::new(),
            message_error: false,
            confirm_forget: false,
            confirm_enroll: false,
            error: String::new(),
            logo,
        };
        if let Err(error) = app.config.ensure_master_key() {
            app.error = format!("Could not prepare the master key: {error}");
        }
        app.key = app.config.master_key().ok();
        app.refresh();
        app
    }

    fn refresh(&mut self) {
        self.drives = drives::scan(false);
        if self.selected.is_none() {
            self.selected = self.drives.first().map(|drive| drive.letter);
        }
        if let Some(selected) = self.selected
            && !self.drives.iter().any(|drive| drive.letter == selected)
        {
            self.selected = self.drives.first().map(|drive| drive.letter);
        }
        self.guard = GuardState::load();
        self.agent_ok = AgentClient::connect()
            .ok()
            .map(|client| client.health())
            .unwrap_or(false);
        self.last_refresh = Instant::now();
    }

    fn selected_drive(&self) -> Option<DriveInfo> {
        let selected = self.selected?;
        self.drives
            .iter()
            .find(|drive| drive.letter == selected)
            .cloned()
    }

    fn set_message(&mut self, text: impl Into<String>, is_error: bool) {
        self.message = text.into();
        self.message_error = is_error;
    }

    fn save(&mut self) {
        if let Err(error) = self.config.save() {
            self.set_message(format!("Could not save settings: {error}"), true);
        }
    }

    fn enroll_selected(&mut self, force: bool) {
        let Some(drive) = self.selected_drive() else {
            return;
        };
        match ops::enroll(&drive, &mut self.config, force) {
            Ok(id) => {
                self.key = self.config.master_key().ok();
                self.set_message(
                    format!("Enrolled {} as key {id}.", drive.display_name()),
                    false,
                );
                self.confirm_enroll = false;
                let _ = GuardState::request_stop();
                for _ in 0..15 {
                    std::thread::sleep(Duration::from_millis(200));
                    if !GuardState::load().is_fresh() {
                        break;
                    }
                }
                let _ = startup::spawn_guard();
                self.refresh();
            }
            Err(error) => {
                self.confirm_enroll = false;
                self.set_message(format!("Enroll failed: {error}"), true);
            }
        }
    }

    fn forget_selected(&mut self) {
        let Some(drive) = self.selected_drive() else {
            return;
        };
        match ops::forget(&drive, &mut self.config, false) {
            Ok(Some(id)) => {
                self.set_message(
                    format!("Forgot key {id} on {}.", drive.display_name()),
                    false,
                );
            }
            Ok(None) => self.set_message("No key binding was found on that drive.", true),
            Err(error) => self.set_message(format!("Forget failed: {error}"), true),
        }
        self.confirm_forget = false;
        self.refresh();
    }

    fn guard_running(&self) -> bool {
        self.guard.is_fresh()
    }
}

impl eframe::App for KeyApp {
    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        let ctx = ui.ctx().clone();
        if self.last_refresh.elapsed() > Duration::from_secs(4) {
            self.refresh();
        }
        ctx.request_repaint_after(Duration::from_secs(3));

        egui::Panel::top("header")
            .frame(
                egui::Frame::new()
                    .fill(SURFACE)
                    .stroke(Stroke::new(1.0, BORDER))
                    .inner_margin(egui::Margin::symmetric(24, 14)),
            )
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    if let Some(logo) = &self.logo {
                        ui.add(
                            egui::Image::new(egui::load::SizedTexture::new(
                                logo.id(),
                                egui::vec2(46.0, 46.0),
                            ))
                            .corner_radius(12),
                        );
                        ui.add_space(4.0);
                    }
                    ui.vertical(|ui| {
                        ui.label(RichText::new("SENTINEL").size(10.0).strong().color(ACCENT));
                        ui.label(RichText::new("USB Key").size(24.0).strong().color(TEXT));
                        ui.label(
                            RichText::new("Encrypted physical presence key")
                                .size(11.0)
                                .color(MUTED),
                        );
                    });

                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if self.guard.present {
                            pill(ui, "KEY PRESENT", SUCCESS, SUCCESS_BG, SUCCESS_BORDER);
                        }
                        if self.agent_ok {
                            pill(ui, "AGENT ONLINE", SUCCESS, SUCCESS_BG, SUCCESS_BORDER);
                        } else {
                            pill(ui, "AGENT OFFLINE", WARNING, WARNING_BG, WARNING_BORDER);
                        }
                        if self.guard_running() {
                            pill(ui, "GUARD RUNNING", SUCCESS, SUCCESS_BG, SUCCESS_BORDER);
                        } else {
                            pill(ui, "GUARD STOPPED", WARNING, WARNING_BG, WARNING_BORDER);
                        }
                    });
                });
            });

        egui::CentralPanel::default()
            .frame(
                egui::Frame::new()
                    .fill(BG)
                    .inner_margin(egui::Margin::symmetric(24, 18)),
            )
            .show(ui, |ui| {
                egui::ScrollArea::vertical().show(ui, |ui| {
                    ui.set_max_width(1080.0);
                    if !self.error.is_empty() {
                        banner(ui, &self.error, DANGER, DANGER_BG, Color32::from_rgb(74, 34, 40));
                        ui.add_space(10.0);
                    }

                    card(ui, |ui| {
                        section_header(
                            ui,
                            "Key drives",
                            "Select the USB drive you want Sentinel to watch.",
                        );
                        ui.add_space(6.0);
                        if ui.add(ghost_button("Refresh")).clicked() {
                            self.refresh();
                        }
                        ui.add_space(6.0);
                        if self.drives.is_empty() {
                            banner(
                                ui,
                                "No removable drives found. Plug the USB drive in and press Refresh.",
                                MUTED,
                                INPUT,
                                BORDER,
                            );
                            return;
                        }

                        egui::Grid::new("drive-grid")
                            .num_columns(5)
                            .striped(false)
                            .spacing([18.0, 10.0])
                            .show(ui, |ui| {
                                grid_header(ui, "Drive");
                                grid_header(ui, "Size");
                                grid_header(ui, "Filesystem");
                                grid_header(ui, "Key state");
                                grid_header(ui, "Volume");
                                ui.end_row();

                                let rows: Vec<_> = self
                                    .drives
                                    .iter()
                                    .map(|drive| {
                                        (
                                            drive.clone(),
                                            ops::drive_state(
                                                drive,
                                                &self.config,
                                                self.key.as_ref(),
                                            ),
                                        )
                                    })
                                    .collect();
                                for (drive, state) in rows {
                                    let selected = self.selected == Some(drive.letter);
                                    if ui
                                        .selectable_label(selected, drive.display_name())
                                        .clicked()
                                    {
                                        self.selected = Some(drive.letter);
                                    }
                                    ui.label(RichText::new(drive.size_text()).color(MUTED));
                                    ui.label(
                                        RichText::new(if drive.filesystem.is_empty() {
                                            "unknown"
                                        } else {
                                            drive.filesystem.as_str()
                                        })
                                        .color(MUTED),
                                    );
                                    let (label, fg, bg, border) = state_style(&state);
                                    pill(ui, label, fg, bg, border);
                                    ui.label(
                                        RichText::new(drive.volume_serial_text()).color(MUTED),
                                    );
                                    ui.end_row();
                                }
                            });
                    });

                    ui.add_space(12.0);

                    if let Some(drive) = self.selected_drive() {
                        let state = ops::drive_state(&drive, &self.config, self.key.as_ref());
                        card(ui, |ui| {
                            ui.horizontal(|ui| {
                                ui.label(
                                    RichText::new(format!("Selected - {}", drive.display_name()))
                                        .size(15.0)
                                        .strong()
                                        .color(TEXT),
                                );
                                let (label, fg, bg, border) = state_style(&state);
                                pill(ui, label, fg, bg, border);
                            });
                            ui.add_space(8.0);
                            detail_row(ui, "Fingerprint", &drive.fingerprint_text());
                            detail_row(
                                ui,
                                "Marker",
                                &drive.marker_path().display().to_string(),
                            );
                            ui.add_space(12.0);
                            ui.horizontal(|ui| {
                                match &state {
                                    DriveState::Enrolled(_) => {
                                        if ui
                                            .add(ghost_button("Re-enroll"))
                                            .on_hover_text("Rewrite the marker with a fresh nonce")
                                            .clicked()
                                        {
                                            self.enroll_selected(false);
                                        }
                                    }
                                    DriveState::Available | DriveState::MissingMarker => {
                                        if ui.add(primary_button("Enroll this drive")).clicked() {
                                            self.enroll_selected(false);
                                        }
                                    }
                                    DriveState::Foreign(_) | DriveState::Encrypted(_) => {
                                        if ui.add(primary_button("Replace existing key")).clicked() {
                                            self.confirm_enroll = true;
                                        }
                                    }
                                }
                                if ui
                                    .add_enabled(
                                        !matches!(state, DriveState::Available),
                                        danger_button("Forget key"),
                                    )
                                    .clicked()
                                {
                                    self.confirm_forget = true;
                                }
                            });
                        });
                        ui.add_space(12.0);
                    }

                    card(ui, |ui| {
                        section_header(
                            ui,
                            "When the key is removed",
                            "The guard waits out the grace period before doing anything.",
                        );
                        ui.add_space(12.0);
                        egui::Grid::new("remove-policy")
                            .num_columns(2)
                            .spacing([18.0, 10.0])
                            .show(ui, |ui| {
                                ui.label(RichText::new("Action").color(MUTED));
                                egui::ComboBox::from_id_salt("on_remove")
                                    .width(320.0)
                                    .selected_text(lock_label(&self.config.policy.on_remove))
                                    .show_ui(ui, |ui| {
                                        for (value, label) in [
                                            ("secure", "Sentinel secure lock (2FA overlay)"),
                                            ("windows", "Lock Windows"),
                                            ("away", "Arm Sentinel away mode"),
                                            ("none", "Do nothing"),
                                        ] {
                                            if ui
                                                .selectable_value(
                                                    &mut self.config.policy.on_remove,
                                                    value.to_string(),
                                                    label,
                                                )
                                                .changed()
                                            {
                                                self.save();
                                            }
                                        }
                                    });
                                ui.end_row();

                                ui.label(RichText::new("Grace period").color(MUTED));
                                ui.horizontal(|ui| {
                                    let changed = ui
                                        .add(
                                            egui::DragValue::new(
                                                &mut self.config.policy.grace_seconds,
                                            )
                                            .range(0..=600)
                                            .suffix(" s"),
                                        )
                                        .changed();
                                    if changed {
                                        self.save();
                                    }
                                    ui.label(
                                        RichText::new("time to plug it back in")
                                            .size(11.0)
                                            .color(MUTED),
                                    );
                                });
                                ui.end_row();
                            });
                        ui.add_space(10.0);
                        if ui
                            .checkbox(
                                &mut self.config.policy.lock_on_startup,
                                "Lock after startup if the key is not present",
                            )
                            .changed()
                        {
                            self.save();
                        }
                        ui.add_space(8.0);
                        if ui.add(ghost_button("Save policy")).clicked() {
                            self.save();
                            self.set_message("Policy saved.", false);
                        }
                    });

                    ui.add_space(12.0);

                    card(ui, |ui| {
                        section_header(
                            ui,
                            "When the key is inserted",
                            "The safe default keeps the 2FA code as the second factor.",
                        );
                        ui.add_space(12.0);
                        egui::ComboBox::from_id_salt("on_insert")
                            .width(420.0)
                            .selected_text(unlock_label(&self.config.policy.on_insert))
                            .show_ui(ui, |ui| {
                                for (value, label) in [
                                    ("key+code", "Mark present; still require the 2FA code"),
                                    ("key", "Release Sentinel secure lock with the key alone"),
                                    ("none", "Do nothing"),
                                ] {
                                    if ui
                                        .selectable_value(
                                            &mut self.config.policy.on_insert,
                                            value.to_string(),
                                            label,
                                        )
                                        .changed()
                                    {
                                        self.save();
                                    }
                                }
                            });
                        if self.config.policy.on_insert == "key" {
                            ui.add_space(10.0);
                            banner(
                                ui,
                                "Key-only release is weaker: the drive becomes a single factor.",
                                WARNING,
                                WARNING_BG,
                                WARNING_BORDER,
                            );
                        }
                    });

                    ui.add_space(12.0);

                    card(ui, |ui| {
                        section_header(ui, "Guard", "The background watcher for this key.");
                        ui.add_space(8.0);
                        ui.label(
                            RichText::new(if self.guard_running() {
                                format!(
                                    "Running (pid {}). Last event: {}",
                                    self.guard.pid, self.guard.last_event
                                )
                            } else {
                                "Not running. Start it after enrolling a drive."
                                    .to_string()
                            })
                            .color(if self.guard_running() { SUCCESS } else { MUTED }),
                        );
                        ui.add_space(12.0);
                        ui.horizontal(|ui| {
                            if ui
                                .add_enabled(
                                    !self.guard_running(),
                                    primary_button("Start guard"),
                                )
                                .clicked()
                            {
                                match startup::spawn_guard() {
                                    Ok(()) => {
                                        self.set_message("Guard starting...", false);
                                        std::thread::sleep(Duration::from_millis(600));
                                        self.refresh();
                                    }
                                    Err(error) => self.set_message(
                                        format!("Could not start guard: {error}"),
                                        true,
                                    ),
                                }
                            }
                            if ui
                                .add_enabled(
                                    self.guard_running(),
                                    ghost_button("Stop guard"),
                                )
                                .clicked()
                            {
                                let _ = GuardState::request_stop();
                                self.set_message("Stop requested.", false);
                            }
                            if ui.add(ghost_button("Pause 10 min")).clicked() {
                                match GuardState::pause_for(10) {
                                    Ok(until) => self.set_message(
                                        format!("Paused until {}.", config::timestamp_text(until)),
                                        false,
                                    ),
                                    Err(error) => {
                                        self.set_message(format!("Pause failed: {error}"), true)
                                    }
                                }
                            }
                            if ui.add(ghost_button("Resume")).clicked() {
                                let _ = GuardState::clear_pause();
                                self.set_message("Pause cleared.", false);
                            }
                        });
                        ui.add_space(10.0);
                        let mut at_login = startup::is_enabled();
                        if ui
                            .checkbox(&mut at_login, "Start the guard with Windows")
                            .changed()
                        {
                            if let Err(error) = startup::set(at_login) {
                                self.set_message(
                                    format!("Startup change failed: {error}"),
                                    true,
                                );
                            } else {
                                self.config.start_guard_at_login = at_login;
                                self.save();
                            }
                        }
                    });

                    if !self.message.is_empty() {
                        ui.add_space(12.0);
                        banner(
                            ui,
                            &self.message,
                            if self.message_error { DANGER } else { SUCCESS },
                            if self.message_error {
                                DANGER_BG
                            } else {
                                SUCCESS_BG
                            },
                            if self.message_error {
                                Color32::from_rgb(74, 34, 40)
                            } else {
                                SUCCESS_BORDER
                            },
                        );
                    }

                    ui.add_space(12.0);
                    egui::Frame::new()
                        .fill(SURFACE)
                        .stroke(Stroke::new(1.0, BORDER))
                        .corner_radius(14)
                        .inner_margin(egui::Margin::same(16))
                        .show(ui, |ui| {
                            ui.label(
                                RichText::new("SECURITY NOTE")
                                    .size(9.0)
                                    .strong()
                                    .color(WARNING),
                            );
                            ui.add_space(4.0);
                            ui.label(
                                RichText::new(
                                    "A flash drive is a presence token, not a FIDO2 authenticator. \
                                     The encrypted marker prevents casual reading and stops a copied \
                                     marker from working on another drive, but someone who steals the \
                                     drive and has this Windows account can still use it. Keep the 2FA \
                                     code as the fallback. Only SENTINEL.KEY is encrypted; the rest of \
                                     the drive is normal storage.",
                                )
                                .size(11.0)
                                .color(MUTED),
                            );
                        });
                    ui.add_space(20.0);
                });
            });

        if self.confirm_forget {
            let mut open = true;
            egui::Window::new("Forget this key?")
                .collapsible(false)
                .resizable(false)
                .open(&mut open)
                .show(&ctx, |ui| {
                    ui.label(
                        "This deletes the encrypted marker from the drive and removes the binding from this PC.",
                    );
                    ui.add_space(10.0);
                    ui.horizontal(|ui| {
                        if ui.add(danger_button("Forget key")).clicked() {
                            self.forget_selected();
                        }
                        if ui.add(ghost_button("Cancel")).clicked() {
                            self.confirm_forget = false;
                        }
                    });
                });
            if !open {
                self.confirm_forget = false;
            }
        }

        if self.confirm_enroll {
            let mut open = true;
            egui::Window::new("Replace the existing key?")
                .collapsible(false)
                .resizable(false)
                .open(&mut open)
                .show(&ctx, |ui| {
                    ui.label(
                        "This drive already contains a Sentinel marker. Replacing it makes the old marker useless.",
                    );
                    ui.add_space(10.0);
                    ui.horizontal(|ui| {
                        if ui.add(primary_button("Replace key")).clicked() {
                            self.enroll_selected(true);
                        }
                        if ui.add(ghost_button("Cancel")).clicked() {
                            self.confirm_enroll = false;
                        }
                    });
                });
            if !open {
                self.confirm_enroll = false;
            }
        }
    }
}

fn install_style(ctx: &egui::Context) {
    ctx.set_theme(egui::ThemePreference::Dark);
    let mut style = (*ctx.style_of(egui::Theme::Dark)).clone();
    let mut visuals = egui::Visuals::dark();
    visuals.panel_fill = BG;
    visuals.window_fill = SURFACE;
    visuals.extreme_bg_color = INPUT;
    visuals.faint_bg_color = SURFACE;
    visuals.window_stroke = Stroke::new(1.0, BORDER);
    visuals.window_corner_radius = egui::CornerRadius::same(18);
    visuals.menu_corner_radius = egui::CornerRadius::same(12);
    visuals.override_text_color = Some(TEXT);
    visuals.hyperlink_color = ACCENT;
    visuals.warn_fg_color = WARNING;
    visuals.error_fg_color = DANGER;
    visuals.selection.bg_fill = ACCENT_DIM;
    visuals.selection.stroke = Stroke::new(1.0, TEXT);

    let widgets = &mut visuals.widgets;
    widgets.noninteractive.bg_fill = SURFACE;
    widgets.noninteractive.weak_bg_fill = SURFACE;
    widgets.noninteractive.bg_stroke = Stroke::new(1.0, BORDER);
    widgets.noninteractive.fg_stroke = Stroke::new(1.0, TEXT);
    widgets.noninteractive.corner_radius = egui::CornerRadius::same(10);

    widgets.inactive.bg_fill = ELEVATED;
    widgets.inactive.weak_bg_fill = ELEVATED;
    widgets.inactive.bg_stroke = Stroke::new(1.0, BORDER);
    widgets.inactive.fg_stroke = Stroke::new(1.0, TEXT);
    widgets.inactive.corner_radius = egui::CornerRadius::same(10);

    widgets.hovered.bg_fill = Color32::from_rgb(27, 33, 48);
    widgets.hovered.weak_bg_fill = Color32::from_rgb(27, 33, 48);
    widgets.hovered.bg_stroke = Stroke::new(1.0, BORDER_HOVER);
    widgets.hovered.fg_stroke = Stroke::new(1.0, TEXT);
    widgets.hovered.corner_radius = egui::CornerRadius::same(10);

    widgets.active.bg_fill = Color32::from_rgb(20, 25, 36);
    widgets.active.weak_bg_fill = Color32::from_rgb(20, 25, 36);
    widgets.active.bg_stroke = Stroke::new(1.0, ACCENT);
    widgets.active.fg_stroke = Stroke::new(1.0, TEXT);
    widgets.active.corner_radius = egui::CornerRadius::same(10);

    style.visuals = visuals;
    style.spacing.item_spacing = egui::vec2(10.0, 8.0);
    style.spacing.button_padding = egui::vec2(14.0, 8.0);
    style.spacing.interact_size = egui::vec2(40.0, 32.0);
    style.spacing.window_margin = egui::Margin::same(16);
    style.spacing.menu_margin = egui::Margin::same(8);
    style.text_styles.insert(
        egui::TextStyle::Body,
        egui::FontId::new(13.0, egui::FontFamily::Proportional),
    );
    style.text_styles.insert(
        egui::TextStyle::Button,
        egui::FontId::new(13.0, egui::FontFamily::Proportional),
    );
    style.text_styles.insert(
        egui::TextStyle::Heading,
        egui::FontId::new(24.0, egui::FontFamily::Proportional),
    );
    style.text_styles.insert(
        egui::TextStyle::Monospace,
        egui::FontId::new(12.0, egui::FontFamily::Monospace),
    );
    style.text_styles.insert(
        egui::TextStyle::Small,
        egui::FontId::new(10.0, egui::FontFamily::Proportional),
    );
    ctx.set_style_of(egui::Theme::Dark, style);
}

fn load_logo(ctx: &egui::Context) -> Option<egui::TextureHandle> {
    let bytes = include_bytes!("../../agent/Sentinel/Assets/logo.png");
    let image = image::load_from_memory(bytes).ok()?.to_rgba8();
    let size = [image.width() as usize, image.height() as usize];
    let color = egui::ColorImage::from_rgba_unmultiplied(size, image.as_raw());
    Some(ctx.load_texture("sentinel-logo", color, egui::TextureOptions::LINEAR))
}

fn card<R>(ui: &mut egui::Ui, add: impl FnOnce(&mut egui::Ui) -> R) -> R {
    egui::Frame::new()
        .fill(SURFACE)
        .stroke(Stroke::new(1.0, BORDER))
        .corner_radius(18)
        .inner_margin(egui::Margin::same(18))
        .show(ui, |ui| {
            ui.set_min_width((ui.available_width() - 12.0).max(0.0));
            add(ui)
        })
        .inner
}

fn section_header(ui: &mut egui::Ui, title: &str, subtitle: &str) {
    ui.label(RichText::new(title).size(16.0).strong().color(TEXT));
    ui.add_space(2.0);
    ui.label(RichText::new(subtitle).size(11.0).color(MUTED));
}

fn grid_header(ui: &mut egui::Ui, text: &str) {
    ui.label(RichText::new(text).size(10.0).strong().color(MUTED));
}

fn detail_row(ui: &mut egui::Ui, label: &str, value: &str) {
    ui.horizontal(|ui| {
        ui.add_sized(
            [90.0, 18.0],
            egui::Label::new(RichText::new(label).size(11.0).color(MUTED)),
        );
        ui.label(RichText::new(value).size(11.0).color(TEXT));
    });
}

fn pill(ui: &mut egui::Ui, text: &str, fg: Color32, bg: Color32, border: Color32) {
    let font = egui::FontId::proportional(9.0);
    let galley = ui.painter().layout_no_wrap(text.to_string(), font, fg);
    let size = egui::vec2(galley.size().x + 18.0, galley.size().y + 8.0);
    let (rect, _) = ui.allocate_exact_size(size, egui::Sense::hover());
    ui.painter()
        .rect_filled(rect, egui::CornerRadius::same(10), bg);
    ui.painter().rect_stroke(
        rect,
        egui::CornerRadius::same(10),
        Stroke::new(1.0, border),
        egui::StrokeKind::Inside,
    );
    let position = rect.center() - galley.size() / 2.0;
    ui.painter().galley(position, galley, fg);
}

fn banner(ui: &mut egui::Ui, text: &str, fg: Color32, bg: Color32, border: Color32) {
    egui::Frame::new()
        .fill(bg)
        .stroke(Stroke::new(1.0, border))
        .corner_radius(12)
        .inner_margin(egui::Margin::symmetric(14, 10))
        .show(ui, |ui| {
            ui.label(RichText::new(text).size(11.0).color(fg));
        });
}

fn primary_button(text: &'static str) -> egui::Button<'static> {
    egui::Button::new(RichText::new(text).color(Color32::WHITE).strong())
        .fill(ACCENT)
        .stroke(Stroke::NONE)
        .corner_radius(10)
        .min_size(egui::vec2(0.0, 34.0))
}

fn ghost_button(text: &'static str) -> egui::Button<'static> {
    egui::Button::new(RichText::new(text).color(TEXT))
        .fill(ELEVATED)
        .stroke(Stroke::new(1.0, BORDER))
        .corner_radius(10)
        .min_size(egui::vec2(0.0, 34.0))
}

fn danger_button(text: &'static str) -> egui::Button<'static> {
    egui::Button::new(RichText::new(text).color(DANGER))
        .fill(DANGER_BG)
        .stroke(Stroke::new(1.0, Color32::from_rgb(74, 34, 40)))
        .corner_radius(10)
        .min_size(egui::vec2(0.0, 34.0))
}

fn state_style(state: &DriveState) -> (&'static str, Color32, Color32, Color32) {
    match state {
        DriveState::Available => ("not enrolled", MUTED, INPUT, BORDER),
        DriveState::Enrolled(_) => ("enrolled", SUCCESS, SUCCESS_BG, SUCCESS_BORDER),
        DriveState::Foreign(_) => ("other PC", WARNING, WARNING_BG, WARNING_BORDER),
        DriveState::Encrypted(_) => ("encrypted", WARNING, WARNING_BG, WARNING_BORDER),
        DriveState::MissingMarker => (
            "marker missing",
            DANGER,
            DANGER_BG,
            Color32::from_rgb(74, 34, 40),
        ),
    }
}

fn lock_label(value: &str) -> &'static str {
    match value {
        "windows" => "Lock Windows",
        "away" => "Arm Sentinel away mode",
        "none" => "Do nothing",
        _ => "Sentinel secure lock",
    }
}

fn unlock_label(value: &str) -> &'static str {
    match value {
        "key" => "Release secure lock with the key alone",
        "none" => "Do nothing",
        _ => "Mark present; still require the 2FA code",
    }
}
