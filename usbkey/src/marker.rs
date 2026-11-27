use std::fs;
use std::path::Path;

use aes_gcm::aead::{Aead, KeyInit, Payload};
use aes_gcm::{Aes256Gcm, Nonce};
use anyhow::{Context, Result, anyhow};
use serde::{Deserialize, Serialize};

use crate::config::now_ms;
use crate::drives::{DriveInfo, normalize_serial};

pub const MARKER_NAME: &str = "SENTINEL.KEY";
pub const README_NAME: &str = "SENTINEL-KEY-README.txt";
pub const MAGIC: &[u8; 4] = b"SNTK";
pub const FORMAT_VERSION: u8 = 2;
pub const LEGACY_FORMAT_VERSION: u8 = 1;
const HEADER_LEN: usize = 4 + 1 + 1 + 12 + 4;
const MAX_MARKER: u64 = 16 * 1024;

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct MarkerPayload {
    pub v: u32,
    pub id: String,
    pub label: String,
    pub pc: String,
    pub enrolled_at: i64,
    pub volume_serial: u32,
    pub total_bytes: u64,
    pub drive_serial: String,
    pub vendor: String,
    pub product: String,
    pub revision: String,
    pub volume_guid: String,
    pub filesystem: String,
}

pub fn aad_v1(drive: &DriveInfo) -> Vec<u8> {
    format!(
        "SNTK|{}|{}|{}|{}|{}",
        LEGACY_FORMAT_VERSION,
        normalize_serial(&drive.disk_serial),
        drive.volume_serial,
        drive.total_bytes,
        drive.filesystem.to_ascii_uppercase()
    )
    .into_bytes()
}

pub fn aad_v2(drive: &DriveInfo) -> Vec<u8> {
    format!(
        "SNTK|{}|{}|{}|{}|{}|{}|{}|{}",
        FORMAT_VERSION,
        normalize_serial(&drive.disk_serial),
        normalize_serial(&drive.vendor),
        normalize_serial(&drive.product),
        normalize_serial(&drive.revision),
        normalize_serial(&drive.volume_guid),
        drive.volume_serial,
        drive.total_bytes
    )
    .into_bytes()
}

pub fn encrypt_marker(
    payload: &MarkerPayload,
    key: &[u8; 32],
    drive: &DriveInfo,
) -> Result<Vec<u8>> {
    let mut nonce_bytes = [0u8; 12];
    getrandom::fill(&mut nonce_bytes).map_err(|error| anyhow!("generate marker nonce: {error}"))?;
    let plaintext = serde_json::to_vec(payload)?;
    let cipher = Aes256Gcm::new_from_slice(key).map_err(|_| anyhow!("bad AES key"))?;
    let nonce = Nonce::from(nonce_bytes);
    let ciphertext = cipher
        .encrypt(
            &nonce,
            Payload {
                msg: &plaintext,
                aad: &aad_v2(drive),
            },
        )
        .map_err(|_| anyhow!("marker encryption failed"))?;

    let mut output = Vec::with_capacity(HEADER_LEN + ciphertext.len());
    output.extend_from_slice(MAGIC);
    output.push(FORMAT_VERSION);
    output.push(0);
    output.extend_from_slice(&nonce_bytes);
    output.extend_from_slice(&(ciphertext.len() as u32).to_le_bytes());
    output.extend_from_slice(&ciphertext);
    Ok(output)
}

pub fn decrypt_marker(data: &[u8], key: &[u8; 32], drive: &DriveInfo) -> Result<MarkerPayload> {
    if data.len() < HEADER_LEN {
        return Err(anyhow!("marker is too short"));
    }
    if &data[0..4] != MAGIC {
        return Err(anyhow!("not a Sentinel key marker"));
    }
    let format_version = data[4];
    if format_version != FORMAT_VERSION && format_version != LEGACY_FORMAT_VERSION {
        return Err(anyhow!("unsupported marker version {format_version}"));
    }
    let mut nonce_bytes = [0u8; 12];
    nonce_bytes.copy_from_slice(&data[6..18]);
    let length = u32::from_le_bytes(data[18..22].try_into().unwrap()) as usize;
    if length == 0 || data.len() < HEADER_LEN + length {
        return Err(anyhow!("marker is truncated"));
    }

    let auth_data = if format_version == LEGACY_FORMAT_VERSION {
        aad_v1(drive)
    } else {
        aad_v2(drive)
    };
    let cipher = Aes256Gcm::new_from_slice(key).map_err(|_| anyhow!("bad AES key"))?;
    let nonce = Nonce::from(nonce_bytes);
    let plaintext = cipher
        .decrypt(
            &nonce,
            Payload {
                msg: &data[HEADER_LEN..HEADER_LEN + length],
                aad: &auth_data,
            },
        )
        .map_err(|_| anyhow!("marker is encrypted for a different drive or PC"))?;
    let payload: MarkerPayload = serde_json::from_slice(&plaintext)?;
    if payload.v != 1 && payload.v != FORMAT_VERSION as u32 {
        return Err(anyhow!("marker belongs to a different Sentinel version"));
    }
    if payload.id.trim().is_empty() {
        return Err(anyhow!("marker has no key id"));
    }
    if payload.volume_serial != drive.volume_serial
        || payload.total_bytes != drive.total_bytes
        || normalize_serial(&payload.drive_serial) != normalize_serial(&drive.disk_serial)
    {
        return Err(anyhow!("marker fingerprint does not match this drive"));
    }
    if format_version >= FORMAT_VERSION
        && (normalize_serial(&payload.vendor) != normalize_serial(&drive.vendor)
            || normalize_serial(&payload.product) != normalize_serial(&drive.product)
            || normalize_serial(&payload.revision) != normalize_serial(&drive.revision)
            || normalize_serial(&payload.volume_guid) != normalize_serial(&drive.volume_guid))
    {
        return Err(anyhow!(
            "marker hardware identity does not match this drive"
        ));
    }
    Ok(payload)
}

pub fn read_marker(drive: &DriveInfo, key: &[u8; 32]) -> Result<MarkerPayload> {
    let path = drive.marker_path();
    let metadata = fs::metadata(&path).with_context(|| format!("read {}", path.display()))?;
    if metadata.len() > MAX_MARKER {
        return Err(anyhow!("marker file is suspiciously large"));
    }
    let data = fs::read(&path).with_context(|| format!("read {}", path.display()))?;
    decrypt_marker(&data, key, drive)
}

pub fn marker_exists(drive: &DriveInfo) -> bool {
    drive.marker_path().is_file()
}

pub fn write_marker(drive: &DriveInfo, key: &[u8; 32], payload: &MarkerPayload) -> Result<()> {
    let data = encrypt_marker(payload, key, drive)?;
    let path = drive.marker_path();
    let temp = path.with_extension("key.tmp");
    fs::write(&temp, &data).with_context(|| format!("write {}", temp.display()))?;
    if path.exists() {
        fs::remove_file(&path).with_context(|| format!("replace {}", path.display()))?;
    }
    fs::rename(&temp, &path).with_context(|| format!("rename {}", path.display()))?;
    write_readme(&drive.root)?;
    Ok(())
}

pub fn remove_marker(drive: &DriveInfo) -> Result<()> {
    let path = drive.marker_path();
    if path.exists() {
        fs::remove_file(&path).with_context(|| format!("delete {}", path.display()))?;
    }
    let readme = Path::new(&drive.root).join(README_NAME);
    if readme.exists() {
        let _ = fs::remove_file(readme);
    }
    Ok(())
}

pub fn new_payload(drive: &DriveInfo, id: String) -> MarkerPayload {
    MarkerPayload {
        v: FORMAT_VERSION as u32,
        id,
        label: drive.label.clone(),
        pc: std::env::var("COMPUTERNAME").unwrap_or_else(|_| "this PC".to_string()),
        enrolled_at: now_ms(),
        volume_serial: drive.volume_serial,
        total_bytes: drive.total_bytes,
        drive_serial: drive.disk_serial.clone(),
        vendor: drive.vendor.clone(),
        product: drive.product.clone(),
        revision: drive.revision.clone(),
        volume_guid: drive.volume_guid.clone(),
        filesystem: drive.filesystem.clone(),
    }
}

fn write_readme(root: &str) -> Result<()> {
    let text = "\
SENTINEL KEY
============

This drive is enrolled as a Sentinel presence key.

SENTINEL.KEY is an encrypted marker. It is bound to this drive's
hardware identity and to the Windows user who enrolled it. Copying
the marker to another drive will not work.

Do not edit or delete SENTINEL.KEY. To remove the key, open the
Sentinel Key app on the enrolled PC and choose Forget.

A USB flash drive is a presence factor, not a cryptographic
authenticator. Anyone who steals the drive can use it while it is
plugged into the enrolled PC.
";
    let path = Path::new(root).join(README_NAME);
    fs::write(&path, text).with_context(|| format!("write {}", path.display()))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn drive() -> DriveInfo {
        DriveInfo {
            letter: 'E',
            root: "E:\\".to_string(),
            label: "TEST".to_string(),
            volume_serial: 0x1234_5678,
            filesystem: "exFAT".to_string(),
            total_bytes: 64_000_000_000,
            disk_serial: "ABC123".to_string(),
            vendor: "GENERIC".to_string(),
            product: "FLASH".to_string(),
            revision: "1.0".to_string(),
            volume_guid: "\\\\?\\Volume{test}\\".to_string(),
            bus_type: 7,
            removable: true,
        }
    }

    #[test]
    fn round_trip() {
        let key = [7u8; 32];
        let payload = new_payload(&drive(), "test-id".to_string());
        let bytes = encrypt_marker(&payload, &key, &drive()).unwrap();
        let decoded = decrypt_marker(&bytes, &key, &drive()).unwrap();
        assert_eq!(decoded.id, "test-id");
        assert_eq!(decoded.total_bytes, payload.total_bytes);
    }

    #[test]
    fn rejects_tamper() {
        let key = [7u8; 32];
        let payload = new_payload(&drive(), "test-id".to_string());
        let mut bytes = encrypt_marker(&payload, &key, &drive()).unwrap();
        let last = bytes.len() - 1;
        bytes[last] ^= 0x40;
        assert!(decrypt_marker(&bytes, &key, &drive()).is_err());
    }

    #[test]
    fn rejects_a_different_drive() {
        let key = [7u8; 32];
        let payload = new_payload(&drive(), "test-id".to_string());
        let bytes = encrypt_marker(&payload, &key, &drive()).unwrap();
        let mut other = drive();
        other.volume_serial = 0x8765_4321;
        assert!(decrypt_marker(&bytes, &key, &other).is_err());
    }

    #[test]
    fn rejects_changed_hardware_identity() {
        let key = [7u8; 32];
        let payload = new_payload(&drive(), "test-id".to_string());
        let bytes = encrypt_marker(&payload, &key, &drive()).unwrap();
        let mut other = drive();
        other.vendor = "OTHER".to_string();
        assert!(decrypt_marker(&bytes, &key, &other).is_err());
    }
}
