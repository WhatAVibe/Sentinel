use std::fs;
use std::path::PathBuf;

use anyhow::Result;
use serde::{Deserialize, Serialize};

use crate::config::{now_ms, pause_path, state_path, stop_path};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct GuardState {
    pub present: bool,
    pub key_id: String,
    pub label: String,
    pub drive: String,
    pub updated_at: i64,
    pub last_event: String,
    pub agent_ok: bool,
    pub paused_until: i64,
    pub pid: u32,
}

impl Default for GuardState {
    fn default() -> Self {
        Self {
            present: false,
            key_id: String::new(),
            label: String::new(),
            drive: String::new(),
            updated_at: 0,
            last_event: "not running".to_string(),
            agent_ok: false,
            paused_until: 0,
            pid: 0,
        }
    }
}

impl GuardState {
    pub fn load() -> Self {
        let path = state_path();
        let Ok(text) = fs::read_to_string(&path) else {
            return Self::default();
        };
        serde_json::from_str(&text).unwrap_or_default()
    }

    pub fn save(&self) -> Result<()> {
        let path = state_path();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let tmp = path.with_extension("json.tmp");
        fs::write(&tmp, serde_json::to_string_pretty(self)?)?;
        fs::rename(&tmp, &path)?;
        Ok(())
    }

    pub fn is_fresh(&self) -> bool {
        now_ms() - self.updated_at < 8_000
    }

    pub fn pause_for(minutes: u64) -> Result<i64> {
        let until = now_ms() + (minutes.min(1440) as i64) * 60_000;
        let mut state = Self::load();
        state.paused_until = until;
        state.save()?;
        fs::write(pause_path(), until.to_string())?;
        Ok(until)
    }

    pub fn clear_pause() -> Result<()> {
        let path = pause_path();
        if path.exists() {
            fs::remove_file(path)?;
        }
        let mut state = Self::load();
        state.paused_until = 0;
        state.save()?;
        Ok(())
    }

    pub fn read_pause_file() -> i64 {
        fs::read_to_string(pause_path())
            .ok()
            .and_then(|text| text.trim().parse::<i64>().ok())
            .unwrap_or(0)
    }

    pub fn request_stop() -> Result<()> {
        fs::write(stop_path(), now_ms().to_string())?;
        Ok(())
    }

    pub fn should_stop() -> bool {
        stop_path().exists()
    }

    pub fn clear_stop() {
        let path: PathBuf = stop_path();
        if path.exists() {
            let _ = fs::remove_file(path);
        }
    }
}
