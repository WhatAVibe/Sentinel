use std::fs;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result, anyhow};
use base64::Engine;
use base64::engine::general_purpose::STANDARD as B64;
use serde::{Deserialize, Serialize};

use crate::platform;

pub const ENTROPY: &[u8] = b"Sentinel.USBKey.v1";
pub const VERSION: u32 = 1;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct UsbKeyConfig {
    pub version: u32,
    pub protected_master_key: String,
    pub devices: Vec<EnrolledDevice>,
    pub policy: Policy,
    pub start_guard_at_login: bool,
}

impl Default for UsbKeyConfig {
    fn default() -> Self {
        Self {
            version: VERSION,
            protected_master_key: String::new(),
            devices: Vec::new(),
            policy: Policy::default(),
            start_guard_at_login: false,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct Policy {
    pub on_remove: String,
    pub on_insert: String,
    pub grace_seconds: u64,
    pub lock_on_startup: bool,
}

impl Default for Policy {
    fn default() -> Self {
        Self {
            on_remove: "secure".to_string(),
            on_insert: "key+code".to_string(),
            grace_seconds: 20,
            lock_on_startup: false,
        }
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct EnrolledDevice {
    pub id: String,
    pub label: String,
    pub drive_serial: String,
    pub volume_serial: u32,
    pub total_bytes: u64,
    pub filesystem: String,
    pub vendor: String,
    pub product: String,
    pub revision: String,
    pub volume_guid: String,
    pub enrolled_at: i64,
    pub last_seen: i64,
}

impl UsbKeyConfig {
    pub fn load_or_create() -> Result<Self> {
        let path = config_path();
        if !path.exists() {
            let config = Self::default();
            config.save()?;
            return Ok(config);
        }
        let text = fs::read_to_string(&path).with_context(|| format!("read {}", path.display()))?;
        let mut config: Self =
            serde_json::from_str(&text).with_context(|| format!("parse {}", path.display()))?;
        config.version = VERSION;
        if config.policy.grace_seconds > 600 {
            config.policy.grace_seconds = 600;
        }
        Ok(config)
    }

    pub fn save(&self) -> Result<()> {
        let path = config_path();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let text = serde_json::to_string_pretty(self)?;
        let tmp = path.with_extension("json.tmp");
        fs::write(&tmp, text)?;
        fs::rename(&tmp, &path)?;
        Ok(())
    }

    pub fn master_key(&self) -> Result<[u8; 32]> {
        if self.protected_master_key.trim().is_empty() {
            return Err(anyhow!("no USB key master key yet"));
        }
        let protected = B64
            .decode(self.protected_master_key.as_bytes())
            .context("decode protected master key")?;
        let plain = platform::dpapi_unprotect(&protected, ENTROPY)
            .context("DPAPI could not unlock the USB key master key")?;
        let key: [u8; 32] = plain
            .as_slice()
            .try_into()
            .map_err(|_| anyhow!("master key has the wrong length"))?;
        Ok(key)
    }

    pub fn ensure_master_key(&mut self) -> Result<[u8; 32]> {
        if !self.protected_master_key.trim().is_empty() {
            return self.master_key();
        }
        let mut key = [0u8; 32];
        getrandom::fill(&mut key).map_err(|error| anyhow!("generate master key: {error}"))?;
        let protected = platform::dpapi_protect(&key, ENTROPY)
            .context("DPAPI could not protect the USB key master key")?;
        self.protected_master_key = B64.encode(protected);
        self.save()?;
        Ok(key)
    }

    pub fn find_device(&self, id: &str) -> Option<&EnrolledDevice> {
        self.devices.iter().find(|device| device.id == id)
    }

    pub fn upsert_device(&mut self, device: EnrolledDevice) {
        if let Some(existing) = self.devices.iter_mut().find(|item| item.id == device.id) {
            *existing = device;
        } else {
            self.devices.push(device);
        }
    }

    pub fn remove_device(&mut self, id: &str) -> bool {
        let before = self.devices.len();
        self.devices.retain(|device| device.id != id);
        before != self.devices.len()
    }
}

pub fn app_data_dir() -> PathBuf {
    std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Sentinel")
}

pub fn config_path() -> PathBuf {
    app_data_dir().join("usbkey.json")
}

pub fn state_path() -> PathBuf {
    app_data_dir().join("usbkey-state.json")
}

pub fn pause_path() -> PathBuf {
    app_data_dir().join("usbkey-pause")
}

pub fn stop_path() -> PathBuf {
    app_data_dir().join("usbkey-stop")
}

pub fn log_path() -> PathBuf {
    app_data_dir().join("logs").join("usbkey.log")
}

pub fn agent_config_path() -> PathBuf {
    app_data_dir().join("config.json")
}

pub fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis() as i64)
        .unwrap_or(0)
}

pub fn now_text() -> String {
    chrono::Local::now().format("%Y-%m-%d %H:%M:%S").to_string()
}

pub fn timestamp_text(ms: i64) -> String {
    chrono::DateTime::from_timestamp_millis(ms)
        .map(|time| {
            time.with_timezone(&chrono::Local)
                .format("%Y-%m-%d %H:%M")
                .to_string()
        })
        .unwrap_or_else(|| "unknown".to_string())
}

pub fn log(line: &str) {
    let path = log_path();
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    if let Ok(meta) = fs::metadata(&path)
        && meta.len() > 1_000_000
    {
        let _ = fs::rename(&path, path.with_extension("log.1"));
    }
    if let Ok(mut file) = fs::OpenOptions::new().create(true).append(true).open(&path) {
        use std::io::Write;
        let _ = writeln!(file, "{} {}", now_text(), line);
    }
}
