use std::time::{Duration, Instant};

use anyhow::{Result, anyhow};

use crate::agent::AgentClient;
use crate::config::{self, UsbKeyConfig};
use crate::drives::{self, DriveInfo};
use crate::marker;
use crate::platform;
use crate::state::GuardState;

const FAST_POLL: Duration = Duration::from_millis(300);

pub fn run() -> Result<()> {
    platform::require_windows()?;
    let _instance = platform::acquire_single_instance("Local\\SentinelUsbKeyGuard")
        .ok_or_else(|| anyhow!("another Sentinel key guard is already running"))?;

    let mut config = UsbKeyConfig::load_or_create()?;
    if config.devices.is_empty() {
        config::log("guard: no enrolled key; exiting");
        return Err(anyhow!("no USB key is enrolled yet"));
    }
    let key = config.ensure_master_key()?;

    GuardState::clear_stop();
    let started = Instant::now();
    let mut state = GuardState {
        pid: std::process::id(),
        last_event: "guard started".to_string(),
        ..GuardState::default()
    };
    state.save()?;
    config::log("guard started");

    let mut agent = AgentClient::connect().ok();
    let mut last_id: Option<String> = None;
    let mut absent_since: Option<Instant> = None;
    let mut locked_for_absence = false;
    let mut last_agent_check = Instant::now() - Duration::from_secs(60);
    let mut last_config_reload = Instant::now();
    let mut last_roots = drives::quick_roots();
    let mut last_full_check = Instant::now() - Duration::from_secs(60);
    let mut last_state_save = Instant::now() - Duration::from_secs(10);
    let mut last_pause_check = Instant::now() - Duration::from_secs(10);
    let mut paused = false;

    loop {
        if GuardState::should_stop() {
            GuardState::clear_stop();
            state.present = false;
            state.last_event = "guard stopped".to_string();
            state.updated_at = config::now_ms();
            state.save()?;
            config::log("guard stopped by request");
            return Ok(());
        }

        if last_config_reload.elapsed() > Duration::from_secs(5) {
            if let Ok(fresh) = UsbKeyConfig::load_or_create() {
                config = fresh;
            }
            last_config_reload = Instant::now();
            if config.devices.is_empty() {
                state.present = false;
                state.key_id = String::new();
                state.last_event = "no keys enrolled".to_string();
                state.updated_at = config::now_ms();
                state.save()?;
                config::log("guard: all keys forgotten; exiting");
                return Ok(());
            }
            let stale = last_id.clone();
            if let Some(id) = stale
                && config.find_device(&id).is_none()
            {
                config::log(&format!("key {id} was forgotten; clearing presence"));
                last_id = None;
                absent_since = None;
                locked_for_absence = false;
                state.present = false;
                state.key_id = String::new();
                state.last_event = "key forgotten".to_string();
                state.updated_at = config::now_ms();
                let _ = state.save();
            }
        }

        if last_pause_check.elapsed() > Duration::from_secs(1) {
            let paused_until = GuardState::read_pause_file().max(GuardState::load().paused_until);
            paused = paused_until > config::now_ms();
            state.paused_until = if paused { paused_until } else { 0 };
            last_pause_check = Instant::now();
        }

        if !paused {
            if last_agent_check.elapsed() > Duration::from_secs(10) {
                if agent.is_none() {
                    agent = AgentClient::connect().ok();
                }
                state.agent_ok = agent.as_ref().map(AgentClient::health).unwrap_or(false);
                last_agent_check = Instant::now();
            }

            let roots = drives::quick_roots();
            let roots_changed = roots != last_roots;
            let due_verify =
                last_id.is_some() && last_full_check.elapsed() > Duration::from_secs(5);
            let initial = last_full_check.elapsed() > Duration::from_secs(30);
            if roots_changed || due_verify || initial {
                last_roots = roots;
                last_full_check = Instant::now();
                let scan_started = Instant::now();
                match find_enrolled_key(&config, &key) {
                    Some((device, drive)) => {
                        let is_new = last_id.as_deref() != Some(device.id.as_str());
                        if is_new {
                            config::log(&format!(
                                "key inserted: {} ({}) id={}",
                                device.label, drive.root, device.id
                            ));
                            state.present = true;
                            state.key_id = device.id.clone();
                            state.label = device.label.clone();
                            state.drive = drive.root.clone();
                            state.last_event = "inserted".to_string();
                            state.updated_at = config::now_ms();
                            state.save()?;

                            if let Some(client) = agent.as_ref() {
                                if let Err(error) =
                                    client.presence(&device.id, true, &device.label, &drive.root)
                                {
                                    config::log(&format!("agent presence failed: {error}"));
                                }
                                if config.policy.on_insert == "key" {
                                    match client.unlock(&device.id) {
                                        Ok(()) => config::log("agent unlocked with USB key"),
                                        Err(error) => {
                                            config::log(&format!("key unlock refused: {error}"))
                                        }
                                    }
                                }
                            }

                            if let Some(known) =
                                config.devices.iter_mut().find(|item| item.id == device.id)
                            {
                                known.last_seen = config::now_ms();
                                let _ = config.save();
                            }

                            last_id = Some(device.id.clone());
                            absent_since = None;
                            locked_for_absence = false;
                        } else if absent_since.is_some() || !state.present || locked_for_absence {
                            config::log("key reinserted before enforcement");
                            absent_since = None;
                            locked_for_absence = false;
                            state.present = true;
                            state.key_id = device.id.clone();
                            state.label = device.label.clone();
                            state.drive = drive.root.clone();
                            state.last_event = "reinserted".to_string();
                            state.updated_at = config::now_ms();
                            state.save()?;
                            if let Some(client) = agent.as_ref()
                                && let Err(error) =
                                    client.presence(&device.id, true, &device.label, &drive.root)
                            {
                                config::log(&format!("agent presence failed: {error}"));
                            }
                        }
                    }
                    None => {
                        let was_present = last_id.is_some();
                        if was_present {
                            let since = *absent_since.get_or_insert_with(Instant::now);
                            let grace = Duration::from_secs(config.policy.grace_seconds.min(600));
                            if !locked_for_absence && since.elapsed() >= grace {
                                let id = last_id.clone().unwrap_or_default();
                                config::log(&format!(
                                    "key absent for {:?}; enforcing {}",
                                    since.elapsed(),
                                    config.policy.on_remove
                                ));
                                state.present = false;
                                state.key_id = String::new();
                                state.last_event = "removed".to_string();
                                state.updated_at = config::now_ms();
                                let _ = state.save();

                                if let Some(client) = agent.as_ref() {
                                    let label = state.label.clone();
                                    let drive = state.drive.clone();
                                    let _ = client.presence(&id, false, &label, &drive);
                                }
                                enforce_lock(&config, agent.as_ref());
                                locked_for_absence = true;
                                last_id = None;
                            } else {
                                state.present = false;
                                state.last_event = format!(
                                    "removed; locking in {}s",
                                    grace.saturating_sub(since.elapsed()).as_secs()
                                );
                            }
                        } else if config.policy.lock_on_startup
                            && !locked_for_absence
                            && started.elapsed()
                                >= Duration::from_secs(config.policy.grace_seconds.min(600))
                        {
                            config::log("startup lock: enrolled key is not present");
                            enforce_lock(&config, agent.as_ref());
                            locked_for_absence = true;
                            state.last_event = "startup lock".to_string();
                        }

                        if !was_present {
                            state.present = false;
                        }
                    }
                }
                let elapsed = scan_started.elapsed();
                if elapsed > Duration::from_millis(50) {
                    config::log(&format!("full key scan took {} ms", elapsed.as_millis()));
                }
            }
        } else {
            state.last_event = "paused".to_string();
        }

        if last_state_save.elapsed() > Duration::from_secs(2) {
            state.updated_at = config::now_ms();
            state.pid = std::process::id();
            let _ = state.save();
            last_state_save = Instant::now();
        }
        std::thread::sleep(FAST_POLL);
    }
}

fn find_enrolled_key(
    config: &UsbKeyConfig,
    key: &[u8; 32],
) -> Option<(crate::config::EnrolledDevice, DriveInfo)> {
    for drive in drives::scan(false) {
        if !marker::marker_exists(&drive) {
            continue;
        }
        let Ok(payload) = marker::read_marker(&drive, key) else {
            continue;
        };
        let Some(device) = config.find_device(&payload.id) else {
            continue;
        };
        if drive.matches_enrolled(device) {
            return Some((device.clone(), drive));
        }
    }
    None
}

fn enforce_lock(config: &UsbKeyConfig, agent: Option<&AgentClient>) {
    if platform::is_shutting_down() {
        config::log("lock skipped: Windows is shutting down");
        return;
    }

    if config.policy.on_remove != "none"
        && let Some(client) = agent
    {
        match client.secure_lock_active() {
            Ok(true) => {
                config::log("secure lock already active; leaving it alone");
                return;
            }
            Ok(false) => {}
            Err(error) => config::log(&format!("could not read secure-lock state: {error}")),
        }
    }

    match config.policy.on_remove.as_str() {
        "none" => {
            config::log("lock policy is none; leaving the session unlocked");
        }
        "windows" => {
            let ok = platform::lock_workstation();
            config::log(if ok {
                "Windows workstation locked"
            } else {
                "Windows lock request failed"
            });
        }
        "away" => {
            let sent = agent
                .map(|client| client.command("away_enable", "Away mode was armed automatically."))
                .transpose();
            match sent {
                Ok(Some(())) => config::log("Sentinel away mode armed"),
                other => {
                    if let Err(error) = other {
                        config::log(&format!("away mode failed: {error}"));
                    }
                    let ok = platform::lock_workstation();
                    config::log(if ok {
                        "away mode unavailable; Windows workstation locked"
                    } else {
                        "away mode and Windows lock both failed"
                    });
                }
            }
        }
        _ => {
            let sent = agent
                .map(|client| {
                    client.command(
                        "lock_screen",
                        "This PC is locked. Enter your Sentinel code to unlock.",
                    )
                })
                .transpose();
            match sent {
                Ok(Some(())) => config::log("Sentinel secure lock engaged"),
                other => {
                    if let Err(error) = other {
                        config::log(&format!("secure lock failed: {error}"));
                    }
                    let ok = platform::lock_workstation();
                    config::log(if ok {
                        "secure lock unavailable; Windows workstation locked"
                    } else {
                        "secure lock and Windows lock both failed"
                    });
                }
            }
        }
    }
}
