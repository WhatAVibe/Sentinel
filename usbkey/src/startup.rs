use std::fs;
use std::os::windows::process::CommandExt;
use std::path::PathBuf;
use std::process::{Command, Stdio};

use anyhow::{Context, Result, anyhow};

use crate::config;

const RUN_KEY: &str = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
const VALUE_NAME: &str = "SentinelKeyGuard";
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

fn guard_script_path() -> PathBuf {
    config::app_data_dir().join("sentinel-key-guard.vbs")
}

pub fn is_enabled() -> bool {
    Command::new("reg.exe")
        .args(["query", RUN_KEY, "/v", VALUE_NAME])
        .creation_flags(CREATE_NO_WINDOW)
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .map(|status| status.success())
        .unwrap_or(false)
}

pub fn set(enabled: bool) -> Result<()> {
    if enabled {
        let exe = std::env::current_exe().context("locate sentinel-key.exe")?;
        let script = guard_script_path();
        if let Some(parent) = script.parent() {
            fs::create_dir_all(parent)?;
        }
        let body = format!(
            "Set shell = CreateObject(\"WScript.Shell\")\r\nshell.Run \"\"\"{}\"\" --guard\", 0, False\r\n",
            exe.display()
        );
        fs::write(&script, body).with_context(|| format!("write {}", script.display()))?;

        let wscript = std::env::var("WINDIR")
            .map(|root| PathBuf::from(root).join("System32").join("wscript.exe"))
            .unwrap_or_else(|_| PathBuf::from(r"C:\Windows\System32\wscript.exe"));
        let value = format!("\"{}\" \"{}\"", wscript.display(), script.display());
        let status = Command::new("reg.exe")
            .args([
                "add", RUN_KEY, "/v", VALUE_NAME, "/t", "REG_SZ", "/d", &value, "/f",
            ])
            .creation_flags(CREATE_NO_WINDOW)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .context("run reg.exe add")?;
        if !status.success() {
            return Err(anyhow!("reg.exe add failed with {status}"));
        }
    } else {
        let status = Command::new("reg.exe")
            .args(["delete", RUN_KEY, "/v", VALUE_NAME, "/f"])
            .creation_flags(CREATE_NO_WINDOW)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .context("run reg.exe delete")?;
        if !status.success() {
            // Deleting a value that is already absent is fine.
        }
        let _ = fs::remove_file(guard_script_path());
    }
    Ok(())
}

pub fn spawn_guard() -> Result<()> {
    let exe = std::env::current_exe().context("locate sentinel-key.exe")?;
    Command::new(exe)
        .arg("--guard")
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .context("start the Sentinel key guard")?;
    Ok(())
}
