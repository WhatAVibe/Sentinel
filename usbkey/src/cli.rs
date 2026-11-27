use anyhow::{Result, anyhow};
use serde_json::json;
use std::fs;

use crate::agent::AgentClient;
use crate::config::{self, UsbKeyConfig};
use crate::drives::{self, DriveInfo};
use crate::marker;
use crate::ops::{self, DriveState};
use crate::platform;
use crate::startup;
use crate::state::GuardState;

pub fn list(args: &[String]) -> Result<()> {
    let json_mode = args.iter().any(|arg| arg == "--json");
    let include_fixed = args.iter().any(|arg| arg == "--all");
    let config = UsbKeyConfig::load_or_create()?;
    let key = config.master_key().ok();
    let rows: Vec<_> = drives::scan(include_fixed)
        .into_iter()
        .map(|drive| drive_row(&drive, &config, key.as_ref()))
        .collect();

    if json_mode {
        println!("{}", serde_json::to_string_pretty(&rows)?);
        return Ok(());
    }
    if rows.is_empty() {
        println!("No removable drives found.");
        return Ok(());
    }
    for row in rows {
        println!(
            "{:<3} {:<22} {:<8} {:<22} {}",
            format!("{}:", row["letter"].as_str().unwrap_or("?")),
            row["label"].as_str().unwrap_or(""),
            row["size"].as_str().unwrap_or(""),
            row["state"].as_str().unwrap_or(""),
            row["fingerprint"].as_str().unwrap_or("")
        );
    }
    Ok(())
}

fn drive_row(
    drive: &DriveInfo,
    config: &UsbKeyConfig,
    key: Option<&[u8; 32]>,
) -> serde_json::Value {
    let (state, key_id, detail) = match ops::drive_state(drive, config, key) {
        DriveState::Enrolled(id) => ("enrolled", id, "ready".to_string()),
        DriveState::Foreign(id) => (
            "foreign",
            id,
            "marker is valid but not enrolled on this PC".to_string(),
        ),
        DriveState::Encrypted(error) => (
            "encrypted",
            String::new(),
            format!("marker present but unreadable: {error}"),
        ),
        DriveState::MissingMarker => (
            "missing-marker",
            String::new(),
            "enrolled binding exists but the marker is gone".to_string(),
        ),
        DriveState::Available => ("available", String::new(), "not enrolled".to_string()),
    };

    json!({
        "letter": drive.letter.to_string(),
        "root": drive.root,
        "label": drive.display_name(),
        "size": drive.size_text(),
        "filesystem": drive.filesystem,
        "volume_serial": drive.volume_serial_text(),
        "disk_serial": drive.disk_serial,
        "vendor": drive.vendor,
        "product": drive.product,
        "revision": drive.revision,
        "volume_guid": drive.volume_guid,
        "bus_type": drive.bus_type,
        "removable": drive.removable,
        "marker": marker::marker_exists(drive),
        "state": state,
        "key_id": key_id,
        "detail": detail,
        "fingerprint": drive.fingerprint_text(),
    })
}

pub fn enroll(args: &[String]) -> Result<()> {
    let force = args.iter().any(|arg| arg == "--force");
    let target = args
        .iter()
        .find(|arg| !arg.starts_with("--"))
        .ok_or_else(|| anyhow!("usage: sentinel-key enroll E: [--force]"))?;
    let drive =
        drives::find(target, false).ok_or_else(|| anyhow!("{target} is not a removable drive"))?;

    let mut config = UsbKeyConfig::load_or_create()?;
    let id = ops::enroll(&drive, &mut config, force)?;

    println!("Enrolled {}", drive.display_name());
    println!("  drive:      {}", drive.root);
    println!("  key id:     {id}");
    println!("  fingerprint: {}", drive.fingerprint_text());
    println!("  marker:     {}", drive.marker_path().display());
    println!(
        "  master key: DPAPI-protected in {}",
        config::config_path().display()
    );
    println!();
    println!("The marker is AES-256-GCM encrypted and bound to this drive.");
    println!("Run `sentinel-key guard` or enable startup in the app to watch it.");
    Ok(())
}

pub fn forget(args: &[String]) -> Result<()> {
    let force = args.iter().any(|arg| arg == "--force");
    let target = args
        .iter()
        .find(|arg| !arg.starts_with("--"))
        .ok_or_else(|| anyhow!("usage: sentinel-key forget E: [--force]"))?;
    let drive =
        drives::find(target, true).ok_or_else(|| anyhow!("drive {target} was not found"))?;

    let mut config = UsbKeyConfig::load_or_create()?;
    match ops::forget(&drive, &mut config, force)? {
        Some(id) => println!("Forgot key {id} on {}", drive.display_name()),
        None => println!(
            "No Sentinel key binding was found for {}",
            drive.display_name()
        ),
    }
    Ok(())
}

pub fn status(args: &[String]) -> Result<()> {
    let json_mode = args.iter().any(|arg| arg == "--json");
    let config = UsbKeyConfig::load_or_create()?;
    let key = config.master_key().ok();
    let present: Vec<_> = ops::present_keys(&config, key.as_ref())
        .into_iter()
        .map(|(device, drive)| {
            json!({
                "id": device.id,
                "label": device.label,
                "drive": drive.root,
                "size": drive.size_text(),
            })
        })
        .collect();
    let guard = GuardState::load();
    let agent = AgentClient::connect().ok().map(|client| client.health());

    if json_mode {
        println!(
            "{}",
            serde_json::to_string_pretty(&json!({
                "config": config::config_path(),
                "devices": config.devices,
                "policy": config.policy,
                "present": present,
                "guard": {
                    "running": guard.is_fresh(),
                    "present": guard.present,
                    "last_event": guard.last_event,
                    "updated_at": guard.updated_at,
                    "paused_until": guard.paused_until,
                    "pid": guard.pid,
                },
                "agent_connected": agent,
                "start_guard_at_login": startup::is_enabled(),
            }))?
        );
        return Ok(());
    }

    println!("Sentinel Key");
    println!("  config: {}", config::config_path().display());
    println!(
        "  guard:  {}",
        if guard.is_fresh() {
            "running"
        } else {
            "not running"
        }
    );
    println!(
        "  agent:  {}",
        match agent {
            Some(true) => "connected",
            _ => "not reachable",
        }
    );
    println!(
        "  policy: remove={} insert={} grace={}s startup_lock={}",
        config.policy.on_remove,
        config.policy.on_insert,
        config.policy.grace_seconds,
        config.policy.lock_on_startup
    );
    if config.devices.is_empty() {
        println!("  no keys enrolled");
    } else {
        for device in &config.devices {
            let is_present = present
                .iter()
                .any(|value| value["id"].as_str() == Some(device.id.as_str()));
            println!(
                "  - {}  id={}  {}",
                device.label,
                device.id,
                if is_present { "PRESENT" } else { "not present" }
            );
        }
    }
    Ok(())
}

pub fn probe(args: &[String]) -> Result<()> {
    let count = args
        .first()
        .and_then(|value| value.parse::<u32>().ok())
        .unwrap_or(5)
        .clamp(1, 50);
    let config = UsbKeyConfig::load_or_create()?;
    let key = config.master_key().ok();
    let mut timings = Vec::new();
    for index in 0..count {
        let started = std::time::Instant::now();
        let drives = drives::scan(false);
        let present = ops::present_keys(&config, key.as_ref());
        let elapsed = started.elapsed();
        timings.push(elapsed);
        println!(
            "scan {}: {:.1} ms, {} drive(s), {} present",
            index + 1,
            elapsed.as_secs_f64() * 1000.0,
            drives.len(),
            present.len()
        );
    }
    let total: std::time::Duration = timings.iter().sum();
    let min = timings.iter().min().copied().unwrap_or_default();
    let max = timings.iter().max().copied().unwrap_or_default();
    println!(
        "average {:.1} ms, min {:.1} ms, max {:.1} ms",
        total.as_secs_f64() * 1000.0 / timings.len() as f64,
        min.as_secs_f64() * 1000.0,
        max.as_secs_f64() * 1000.0
    );
    Ok(())
}

pub fn pause(args: &[String]) -> Result<()> {
    let minutes = args
        .first()
        .and_then(|value| value.parse::<u64>().ok())
        .ok_or_else(|| anyhow!("usage: sentinel-key pause 10"))?;
    let until = GuardState::pause_for(minutes)?;
    println!("Paused until {}", config::timestamp_text(until));
    Ok(())
}

pub fn startup(args: &[String]) -> Result<()> {
    let enabled = match args.first().map(String::as_str) {
        Some("on" | "enable" | "true") => true,
        Some("off" | "disable" | "false") => false,
        _ => return Err(anyhow!("usage: sentinel-key startup on|off")),
    };
    startup::set(enabled)?;
    let mut config = UsbKeyConfig::load_or_create()?;
    config.start_guard_at_login = enabled;
    config.save()?;
    println!(
        "Sentinel key guard will {} start with Windows.",
        if enabled { "now" } else { "no longer" }
    );
    Ok(())
}

pub fn selftest() -> Result<()> {
    platform::require_windows()?;
    let mut secret = [0u8; 32];
    getrandom::fill(&mut secret).map_err(|error| anyhow!("random: {error}"))?;
    let protected = platform::dpapi_protect(&secret, b"Sentinel.UsbKey.Selftest")?;
    let opened = platform::dpapi_unprotect(&protected, b"Sentinel.UsbKey.Selftest")?;
    if opened != secret {
        return Err(anyhow!("DPAPI round trip failed"));
    }

    let test_root =
        std::env::temp_dir().join(format!("sentinel-key-selftest-{}", std::process::id()));
    fs::create_dir_all(&test_root)?;
    let fake = DriveInfo {
        letter: 'E',
        root: format!("{}\\", test_root.display()),
        label: "SELFTEST".to_string(),
        volume_serial: 0xDEAD_BEEF,
        filesystem: "exFAT".to_string(),
        total_bytes: 64_000_000_000,
        disk_serial: "SELFTEST123".to_string(),
        vendor: "SENTINEL".to_string(),
        product: "SELFTEST".to_string(),
        revision: "1.0".to_string(),
        volume_guid: "\\\\?\\Volume{selftest}\\".to_string(),
        bus_type: 7,
        removable: true,
    };
    let mut key = [0u8; 32];
    getrandom::fill(&mut key).map_err(|error| anyhow!("random: {error}"))?;
    let payload = marker::new_payload(&fake, "selftest".to_string());
    marker::write_marker(&fake, &key, &payload)?;
    let decoded = marker::read_marker(&fake, &key)?;
    if decoded.id != "selftest" {
        return Err(anyhow!("marker file round trip failed"));
    }
    let marker_size = fs::metadata(fake.marker_path())?.len();
    marker::remove_marker(&fake)?;
    let _ = fs::remove_dir_all(&test_root);

    let agent = AgentClient::connect().ok().map(|client| client.health());
    println!("DPAPI: ok");
    println!("AES-256-GCM marker: ok ({marker_size} bytes on disk)");
    println!(
        "Agent: {}",
        match agent {
            Some(true) => "connected",
            _ => "not reachable",
        }
    );
    println!("Removable drives: {}", drives::scan(false).len());
    Ok(())
}
