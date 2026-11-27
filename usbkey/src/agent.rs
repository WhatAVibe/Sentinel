use std::fs;
use std::io::{Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::time::Duration;

use anyhow::{Context, Result, anyhow};
use base64::Engine;
use base64::engine::general_purpose::STANDARD as B64;
use serde_json::{Value, json};

use crate::config::agent_config_path;
use crate::platform;

const AGENT_ENTROPY: &[u8] = b"Sentinel.v1";

#[derive(Clone, Debug)]
pub struct AgentClient {
    pub port: u16,
    token: String,
}

impl AgentClient {
    pub fn connect() -> Result<Self> {
        let path = agent_config_path();
        let text = fs::read_to_string(&path)
            .with_context(|| format!("Sentinel agent config not found at {}", path.display()))?;
        let value: Value = serde_json::from_str(&text)?;
        let port = value
            .get("LocalPort")
            .and_then(Value::as_u64)
            .unwrap_or(8790) as u16;
        let protected = value
            .get("ProtectedLocalToken")
            .and_then(Value::as_str)
            .unwrap_or("");
        if protected.is_empty() {
            return Err(anyhow!("Sentinel agent has no local API token yet"));
        }
        let bytes = B64.decode(protected)?;
        let plain = platform::dpapi_unprotect(&bytes, AGENT_ENTROPY)
            .context("DPAPI could not unlock the Sentinel local API token")?;
        let token = String::from_utf8(plain).context("local API token is not UTF-8")?;
        Ok(Self { port, token })
    }

    pub fn health(&self) -> bool {
        self.request("GET", "/local/health", false, None)
            .map(|(status, _)| status == 200)
            .unwrap_or(false)
    }

    pub fn presence(&self, id: &str, present: bool, label: &str, drive: &str) -> Result<()> {
        let body = json!({
            "id": id,
            "present": present,
            "label": label,
            "drive": drive,
        });
        self.expect_ok("/local/key/presence", &body)
    }

    pub fn secure_lock_active(&self) -> Result<bool> {
        let (status, response) = self.request("GET", "/local/status", true, None)?;
        if status != 200 {
            return Err(anyhow!(
                "agent returned {status}: {}",
                trim_response(&response)
            ));
        }
        Ok(parse_secure_lock(&response))
    }

    pub fn unlock(&self, id: &str) -> Result<()> {
        self.expect_ok("/local/key/unlock", &json!({ "id": id }))
    }

    pub fn command(&self, kind: &str, message: &str) -> Result<()> {
        let body = json!({
            "type": kind,
            "args": { "message": message }
        });
        self.expect_ok("/local/command", &body)
    }

    fn expect_ok(&self, path: &str, body: &Value) -> Result<()> {
        let (status, response) = self.request("POST", path, true, Some(body))?;
        if status == 200 {
            Ok(())
        } else {
            Err(anyhow!(
                "agent returned {status}: {}",
                trim_response(&response)
            ))
        }
    }

    fn request(
        &self,
        method: &str,
        path: &str,
        auth: bool,
        body: Option<&Value>,
    ) -> Result<(u16, String)> {
        let address = format!("127.0.0.1:{}", self.port);
        let socket = address
            .to_socket_addrs()?
            .next()
            .ok_or_else(|| anyhow!("could not resolve {address}"))?;
        let mut stream = TcpStream::connect_timeout(&socket, Duration::from_secs(2))
            .with_context(|| format!("connect to Sentinel at {address}"))?;
        stream.set_read_timeout(Some(Duration::from_secs(3)))?;
        stream.set_write_timeout(Some(Duration::from_secs(3)))?;

        let payload = body
            .map(serde_json::to_vec)
            .transpose()?
            .unwrap_or_default();
        let mut request = format!(
            "{method} {path} HTTP/1.1\r\nHost: {address}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n",
            payload.len()
        );
        if auth {
            request.push_str(&format!("Authorization: Bearer {}\r\n", self.token));
        }
        request.push_str("\r\n");
        stream.write_all(request.as_bytes())?;
        if !payload.is_empty() {
            stream.write_all(&payload)?;
        }

        let mut raw = Vec::new();
        stream.read_to_end(&mut raw)?;
        let text = String::from_utf8_lossy(&raw).to_string();
        let (head, response) = text.split_once("\r\n\r\n").unwrap_or((text.as_str(), ""));
        let status = head
            .lines()
            .next()
            .and_then(|line| line.split_whitespace().nth(1))
            .and_then(|value| value.parse::<u16>().ok())
            .ok_or_else(|| anyhow!("Sentinel returned an invalid HTTP response"))?;
        Ok((status, response.to_string()))
    }
}

fn trim_response(value: &str) -> String {
    let trimmed = value.trim();
    if trimmed.len() > 300 {
        format!("{}...", &trimmed[..300])
    } else {
        trimmed.to_string()
    }
}

fn parse_secure_lock(body: &str) -> bool {
    serde_json::from_str::<Value>(body)
        .ok()
        .and_then(|value| {
            value
                .pointer("/device/state/secure_lock")
                .and_then(Value::as_bool)
        })
        .unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::parse_secure_lock;

    #[test]
    fn reads_secure_lock_state() {
        assert!(parse_secure_lock(
            r#"{"device":{"state":{"secure_lock":true}}}"#
        ));
        assert!(!parse_secure_lock(
            r#"{"device":{"state":{"secure_lock":false}}}"#
        ));
        assert!(!parse_secure_lock(r#"{"device":{"state":{}}}"#));
        assert!(!parse_secure_lock("not json"));
    }
}
