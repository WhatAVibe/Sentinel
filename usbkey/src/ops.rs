use anyhow::{Result, anyhow};

use crate::config;
use crate::config::{EnrolledDevice, UsbKeyConfig, now_ms};
use crate::drives::DriveInfo;
use crate::marker;

#[derive(Clone, Debug)]
pub enum DriveState {
    Available,
    Enrolled(String),
    Foreign(String),
    Encrypted(String),
    MissingMarker,
}

pub fn drive_state(drive: &DriveInfo, config: &UsbKeyConfig, key: Option<&[u8; 32]>) -> DriveState {
    if marker::marker_exists(drive) {
        return match key {
            Some(key) => match marker::read_marker(drive, key) {
                Ok(payload) => {
                    if config.find_device(&payload.id).is_some() {
                        DriveState::Enrolled(payload.id)
                    } else {
                        DriveState::Foreign(payload.id)
                    }
                }
                Err(error) => DriveState::Encrypted(error.to_string()),
            },
            None => DriveState::Encrypted("no master key on this PC".to_string()),
        };
    }
    if config
        .devices
        .iter()
        .any(|device| drive.matches_enrolled(device))
    {
        DriveState::MissingMarker
    } else {
        DriveState::Available
    }
}

pub fn enroll(drive: &DriveInfo, config: &mut UsbKeyConfig, force: bool) -> Result<String> {
    let key = config.ensure_master_key()?;
    let existing_id = if marker::marker_exists(drive) {
        match marker::read_marker(drive, &key) {
            Ok(payload) => {
                if config.find_device(&payload.id).is_none() && !force {
                    return Err(anyhow!(
                        "this drive already has a key from another PC; use force to replace it"
                    ));
                }
                Some(payload.id)
            }
            Err(error) if !force => {
                return Err(anyhow!(
                    "an unreadable marker is already on this drive ({error}); use force to replace it"
                ));
            }
            Err(_) => None,
        }
    } else {
        None
    };

    let id = existing_id.unwrap_or_else(new_key_id);
    let payload = marker::new_payload(drive, id.clone());
    marker::write_marker(drive, &key, &payload)?;

    config.upsert_device(EnrolledDevice {
        id: id.clone(),
        label: drive.display_name(),
        drive_serial: drive.disk_serial.clone(),
        volume_serial: drive.volume_serial,
        total_bytes: drive.total_bytes,
        filesystem: drive.filesystem.clone(),
        vendor: drive.vendor.clone(),
        product: drive.product.clone(),
        revision: drive.revision.clone(),
        volume_guid: drive.volume_guid.clone(),
        enrolled_at: now_ms(),
        last_seen: now_ms(),
    });
    config.save()?;
    config::log(&format!(
        "enrolled key {} on {} ({})",
        id,
        drive.root,
        drive.fingerprint_text()
    ));
    Ok(id)
}

pub fn forget(drive: &DriveInfo, config: &mut UsbKeyConfig, force: bool) -> Result<Option<String>> {
    let key = config.master_key().ok();
    let mut removed_id = None;
    if marker::marker_exists(drive) {
        let parsed = key
            .as_ref()
            .and_then(|key| marker::read_marker(drive, key).ok());
        if let Some(payload) = parsed {
            removed_id = Some(payload.id);
            marker::remove_marker(drive)?;
        } else if force {
            marker::remove_marker(drive)?;
        } else {
            return Err(anyhow!(
                "the marker on this drive is not readable by this PC; use force to delete it anyway"
            ));
        }
    }

    let id_to_remove = removed_id.or_else(|| {
        config
            .devices
            .iter()
            .find(|device| drive.matches_enrolled(device))
            .map(|device| device.id.clone())
    });
    if let Some(id) = id_to_remove {
        config.remove_device(&id);
        config.save()?;
        config::log(&format!("forgot key {id} on {}", drive.root));
        if config.devices.is_empty() {
            let _ = crate::state::GuardState::request_stop();
        }
        Ok(Some(id))
    } else {
        Ok(None)
    }
}

pub fn present_keys(
    config: &UsbKeyConfig,
    key: Option<&[u8; 32]>,
) -> Vec<(EnrolledDevice, DriveInfo)> {
    let Some(key) = key else {
        return Vec::new();
    };
    let mut found = Vec::new();
    for drive in crate::drives::scan(false) {
        if !marker::marker_exists(&drive) {
            continue;
        }
        let Ok(payload) = marker::read_marker(&drive, key) else {
            continue;
        };
        if let Some(device) = config.find_device(&payload.id)
            && drive.matches_enrolled(device)
        {
            found.push((device.clone(), drive));
        }
    }
    found
}

fn new_key_id() -> String {
    use base64::Engine;
    use base64::engine::general_purpose::URL_SAFE_NO_PAD;
    let mut bytes = [0u8; 16];
    let _ = getrandom::fill(&mut bytes);
    URL_SAFE_NO_PAD.encode(bytes)
}
