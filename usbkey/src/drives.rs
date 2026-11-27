use crate::platform;

#[derive(Clone, Debug)]
pub struct DriveInfo {
    pub letter: char,
    pub root: String,
    pub label: String,
    pub volume_serial: u32,
    pub filesystem: String,
    pub total_bytes: u64,
    pub disk_serial: String,
    pub vendor: String,
    pub product: String,
    pub revision: String,
    pub volume_guid: String,
    pub bus_type: u32,
    pub removable: bool,
}

impl DriveInfo {
    pub fn display_name(&self) -> String {
        let label = if self.label.trim().is_empty() {
            "Removable drive".to_string()
        } else {
            self.label.trim().to_string()
        };
        format!("{} ({}:)", label, self.letter)
    }

    pub fn size_text(&self) -> String {
        format_bytes(self.total_bytes)
    }

    pub fn volume_serial_text(&self) -> String {
        format!("{:08X}", self.volume_serial)
    }

    pub fn fingerprint_text(&self) -> String {
        let serial = if self.disk_serial.is_empty() {
            "no hardware serial".to_string()
        } else {
            self.disk_serial.clone()
        };
        let model = [self.vendor.as_str(), self.product.as_str()]
            .into_iter()
            .filter(|part| !part.is_empty())
            .collect::<Vec<_>>()
            .join(" ");
        format!(
            "{} / {} / vol {} / {} / {}",
            serial,
            if model.is_empty() {
                "unknown model"
            } else {
                &model
            },
            self.volume_serial_text(),
            self.size_text(),
            self.filesystem
        )
    }

    pub fn marker_path(&self) -> std::path::PathBuf {
        std::path::Path::new(&self.root).join(crate::marker::MARKER_NAME)
    }

    pub fn matches_enrolled(&self, device: &crate::config::EnrolledDevice) -> bool {
        self.volume_serial == device.volume_serial
            && self.total_bytes == device.total_bytes
            && normalize_serial(&self.disk_serial) == normalize_serial(&device.drive_serial)
            && (device.vendor.is_empty()
                || normalize_serial(&self.vendor) == normalize_serial(&device.vendor))
            && (device.product.is_empty()
                || normalize_serial(&self.product) == normalize_serial(&device.product))
            && (device.revision.is_empty()
                || normalize_serial(&self.revision) == normalize_serial(&device.revision))
            && (device.volume_guid.is_empty()
                || normalize_serial(&self.volume_guid) == normalize_serial(&device.volume_guid))
    }
}

pub fn normalize_serial(value: &str) -> String {
    value.trim().to_ascii_uppercase()
}

pub fn scan(include_fixed: bool) -> Vec<DriveInfo> {
    platform::drive_roots(include_fixed)
        .into_iter()
        .filter_map(|(letter, root, kind)| {
            let details = platform::volume_details(&root);
            if details.total_bytes == 0 && details.filesystem.is_empty() {
                return None;
            }
            Some(DriveInfo {
                letter,
                root,
                label: details.label,
                volume_serial: details.serial,
                filesystem: details.filesystem,
                total_bytes: details.total_bytes,
                disk_serial: details.disk_serial,
                vendor: details.vendor,
                product: details.product,
                revision: details.revision,
                volume_guid: details.volume_guid,
                bus_type: details.bus_type,
                removable: kind == platform::DRIVE_REMOVABLE,
            })
        })
        .collect()
}

pub fn quick_roots() -> Vec<(char, String)> {
    platform::drive_roots(false)
        .into_iter()
        .map(|(letter, root, _)| (letter, root))
        .collect()
}

pub fn normalize_input(input: &str) -> String {
    let trimmed = input.trim().trim_matches('"');
    let letter = trimmed
        .chars()
        .find(|c| c.is_ascii_alphabetic())
        .unwrap_or('C')
        .to_ascii_uppercase();
    format!("{letter}:")
}

pub fn find(input: &str, include_fixed: bool) -> Option<DriveInfo> {
    let wanted = normalize_input(input);
    scan(include_fixed)
        .into_iter()
        .find(|drive| format!("{}:", drive.letter) == wanted)
}

pub fn format_bytes(bytes: u64) -> String {
    const UNITS: [&str; 4] = ["B", "KB", "MB", "GB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1024.0 && unit < UNITS.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    if unit == 0 {
        format!("{} {}", bytes, UNITS[unit])
    } else {
        format!("{value:.1} {}", UNITS[unit])
    }
}
