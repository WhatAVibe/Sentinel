use std::ffi::c_void;
use std::io;

use anyhow::{Result, anyhow};

type Handle = *mut c_void;
type Bool = i32;
type Dword = u32;

const INVALID_HANDLE_VALUE: Handle = -1isize as Handle;
const GENERIC_READ: Dword = 0x8000_0000;
const FILE_SHARE_READ: Dword = 0x0000_0001;
const FILE_SHARE_WRITE: Dword = 0x0000_0002;
const OPEN_EXISTING: Dword = 3;
const IOCTL_STORAGE_QUERY_PROPERTY: Dword = 0x002D_1400;
const ERROR_ALREADY_EXISTS: Dword = 183;
const SM_SHUTTINGDOWN: i32 = 0x2000;
const CRYPTPROTECT_UI_FORBIDDEN: Dword = 0x1;

pub const DRIVE_REMOVABLE: u32 = 2;
pub const DRIVE_FIXED: u32 = 3;

#[repr(C)]
struct DataBlob {
    cb_data: Dword,
    pb_data: *mut u8,
}

#[repr(C)]
struct StorageDeviceDescriptor {
    version: Dword,
    size: Dword,
    device_type: u8,
    device_type_modifier: u8,
    removable_media: u8,
    command_queueing: u8,
    vendor_id_offset: Dword,
    product_id_offset: Dword,
    product_revision_offset: Dword,
    serial_number_offset: Dword,
    bus_type: Dword,
    raw_properties_length: Dword,
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetLogicalDrives() -> Dword;
    fn GetDriveTypeW(root: *const u16) -> Dword;
    fn GetVolumeInformationW(
        root: *const u16,
        volume_name: *mut u16,
        volume_name_size: Dword,
        serial: *mut Dword,
        max_component: *mut Dword,
        flags: *mut Dword,
        fs_name: *mut u16,
        fs_name_size: Dword,
    ) -> Bool;
    fn GetDiskFreeSpaceExW(
        directory: *const u16,
        free_bytes_available: *mut u64,
        total_bytes: *mut u64,
        total_free_bytes: *mut u64,
    ) -> Bool;
    fn GetVolumeNameForVolumeMountPointW(
        mount_point: *const u16,
        volume_name: *mut u16,
        volume_name_size: Dword,
    ) -> Bool;
    fn CreateFileW(
        name: *const u16,
        access: Dword,
        share: Dword,
        security: *mut c_void,
        disposition: Dword,
        flags: Dword,
        template: Handle,
    ) -> Handle;
    fn DeviceIoControl(
        device: Handle,
        code: Dword,
        in_buffer: *mut c_void,
        in_size: Dword,
        out_buffer: *mut c_void,
        out_size: Dword,
        bytes_returned: *mut Dword,
        overlapped: *mut c_void,
    ) -> Bool;
    fn CloseHandle(handle: Handle) -> Bool;
    fn GetLastError() -> Dword;
    fn GetSystemMetrics(index: i32) -> i32;
    fn LocalFree(memory: *mut c_void) -> *mut c_void;
    fn CreateMutexW(attributes: *mut c_void, owner: Bool, name: *const u16) -> Handle;
}

#[link(name = "user32")]
unsafe extern "system" {
    fn LockWorkStation() -> Bool;
}

#[link(name = "crypt32")]
unsafe extern "system" {
    fn CryptProtectData(
        data_in: *const DataBlob,
        description: *const u16,
        entropy: *const DataBlob,
        reserved: *mut c_void,
        prompt: *mut c_void,
        flags: Dword,
        data_out: *mut DataBlob,
    ) -> Bool;
    fn CryptUnprotectData(
        data_in: *const DataBlob,
        description: *mut *mut u16,
        entropy: *const DataBlob,
        reserved: *mut c_void,
        prompt: *mut c_void,
        flags: Dword,
        data_out: *mut DataBlob,
    ) -> Bool;
}

fn to_wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

fn read_wide(buffer: &[u16]) -> String {
    let end = buffer.iter().position(|&c| c == 0).unwrap_or(buffer.len());
    String::from_utf16_lossy(&buffer[..end])
}

pub fn dpapi_protect(plain: &[u8], entropy: &[u8]) -> Result<Vec<u8>> {
    let input = DataBlob {
        cb_data: plain.len() as Dword,
        pb_data: plain.as_ptr() as *mut u8,
    };
    let extra = DataBlob {
        cb_data: entropy.len() as Dword,
        pb_data: entropy.as_ptr() as *mut u8,
    };
    let mut output = DataBlob {
        cb_data: 0,
        pb_data: std::ptr::null_mut(),
    };

    let ok = unsafe {
        CryptProtectData(
            &input,
            std::ptr::null(),
            &extra,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
    };
    if ok == 0 {
        return Err(io::Error::last_os_error().into());
    }

    let bytes =
        unsafe { std::slice::from_raw_parts(output.pb_data, output.cb_data as usize).to_vec() };
    unsafe {
        LocalFree(output.pb_data as *mut c_void);
    }
    Ok(bytes)
}

pub fn dpapi_unprotect(protected: &[u8], entropy: &[u8]) -> Result<Vec<u8>> {
    let input = DataBlob {
        cb_data: protected.len() as Dword,
        pb_data: protected.as_ptr() as *mut u8,
    };
    let extra = DataBlob {
        cb_data: entropy.len() as Dword,
        pb_data: entropy.as_ptr() as *mut u8,
    };
    let mut output = DataBlob {
        cb_data: 0,
        pb_data: std::ptr::null_mut(),
    };

    let ok = unsafe {
        CryptUnprotectData(
            &input,
            std::ptr::null_mut(),
            &extra,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
    };
    if ok == 0 {
        return Err(io::Error::last_os_error().into());
    }

    let bytes =
        unsafe { std::slice::from_raw_parts(output.pb_data, output.cb_data as usize).to_vec() };
    unsafe {
        LocalFree(output.pb_data as *mut c_void);
    }
    Ok(bytes)
}

pub fn lock_workstation() -> bool {
    unsafe { LockWorkStation() != 0 }
}

pub fn is_shutting_down() -> bool {
    unsafe { GetSystemMetrics(SM_SHUTTINGDOWN) != 0 }
}

pub fn drive_roots(include_fixed: bool) -> Vec<(char, String, u32)> {
    let mask = unsafe { GetLogicalDrives() };
    let mut roots = Vec::new();
    for index in 0..26u32 {
        if mask & (1 << index) == 0 {
            continue;
        }
        let letter = (b'A' + index as u8) as char;
        let root = format!("{letter}:\\");
        let wide = to_wide(&root);
        let kind = unsafe { GetDriveTypeW(wide.as_ptr()) };
        if kind == DRIVE_REMOVABLE || (include_fixed && kind == DRIVE_FIXED) {
            roots.push((letter, root, kind));
        }
    }
    roots
}

pub struct VolumeDetails {
    pub label: String,
    pub serial: u32,
    pub filesystem: String,
    pub total_bytes: u64,
    pub disk_serial: String,
    pub vendor: String,
    pub product: String,
    pub revision: String,
    pub volume_guid: String,
    pub bus_type: u32,
}

pub fn volume_details(root: &str) -> VolumeDetails {
    let wide = to_wide(root);
    let mut label = vec![0u16; 256];
    let mut filesystem = vec![0u16; 64];
    let mut serial: Dword = 0;
    let mut max_component: Dword = 0;
    let mut flags: Dword = 0;
    let ok = unsafe {
        GetVolumeInformationW(
            wide.as_ptr(),
            label.as_mut_ptr(),
            label.len() as Dword,
            &mut serial,
            &mut max_component,
            &mut flags,
            filesystem.as_mut_ptr(),
            filesystem.len() as Dword,
        )
    };

    let mut total_bytes = 0u64;
    let mut free_bytes = 0u64;
    let mut total_free = 0u64;
    unsafe {
        GetDiskFreeSpaceExW(
            wide.as_ptr(),
            &mut free_bytes,
            &mut total_bytes,
            &mut total_free,
        );
    }

    let (disk_serial, vendor, product, revision, bus_type) = disk_identity(root);
    let volume_guid = volume_guid(root);

    VolumeDetails {
        label: if ok != 0 {
            read_wide(&label)
        } else {
            String::new()
        },
        serial: if ok != 0 { serial } else { 0 },
        filesystem: if ok != 0 {
            read_wide(&filesystem)
        } else {
            String::new()
        },
        total_bytes,
        disk_serial,
        vendor,
        product,
        revision,
        volume_guid,
        bus_type,
    }
}

fn volume_guid(root: &str) -> String {
    let wide = to_wide(root);
    let mut buffer = vec![0u16; 128];
    let ok = unsafe {
        GetVolumeNameForVolumeMountPointW(wide.as_ptr(), buffer.as_mut_ptr(), buffer.len() as Dword)
    };
    if ok == 0 {
        String::new()
    } else {
        read_wide(&buffer)
    }
}

fn disk_identity(root: &str) -> (String, String, String, String, u32) {
    let letter = root.chars().next().unwrap_or('C');
    let path = format!("\\\\.\\{letter}:");
    let wide = to_wide(&path);

    let mut handle = unsafe {
        CreateFileW(
            wide.as_ptr(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            std::ptr::null_mut(),
            OPEN_EXISTING,
            0,
            std::ptr::null_mut(),
        )
    };
    if handle == INVALID_HANDLE_VALUE {
        handle = unsafe {
            CreateFileW(
                wide.as_ptr(),
                0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                std::ptr::null_mut(),
                OPEN_EXISTING,
                0,
                std::ptr::null_mut(),
            )
        };
    }
    if handle == INVALID_HANDLE_VALUE {
        return (
            String::new(),
            String::new(),
            String::new(),
            String::new(),
            0,
        );
    }

    let mut query = [0u8; 12];
    query[0..4].copy_from_slice(&0u32.to_le_bytes());
    query[4..8].copy_from_slice(&0u32.to_le_bytes());

    let mut output = vec![0u8; 1024];
    let mut returned: Dword = 0;
    let ok = unsafe {
        DeviceIoControl(
            handle,
            IOCTL_STORAGE_QUERY_PROPERTY,
            query.as_mut_ptr() as *mut c_void,
            query.len() as Dword,
            output.as_mut_ptr() as *mut c_void,
            output.len() as Dword,
            &mut returned,
            std::ptr::null_mut(),
        )
    };
    unsafe {
        CloseHandle(handle);
    }
    if ok == 0 || returned < 36 {
        return (
            String::new(),
            String::new(),
            String::new(),
            String::new(),
            0,
        );
    }

    let descriptor = unsafe { &*(output.as_ptr() as *const StorageDeviceDescriptor) };
    let bus_type = descriptor.bus_type;
    (
        descriptor_string(&output, descriptor.serial_number_offset, returned),
        descriptor_string(&output, descriptor.vendor_id_offset, returned),
        descriptor_string(&output, descriptor.product_id_offset, returned),
        descriptor_string(&output, descriptor.product_revision_offset, returned),
        bus_type,
    )
}

fn descriptor_string(output: &[u8], offset: u32, returned: u32) -> String {
    let offset = offset as usize;
    if offset == 0 || offset >= returned as usize {
        return String::new();
    }
    let end = output[offset..returned as usize]
        .iter()
        .position(|&byte| byte == 0)
        .map(|position| offset + position)
        .unwrap_or(returned as usize);
    String::from_utf8_lossy(&output[offset..end])
        .trim()
        .to_string()
}

pub struct InstanceGuard {
    handle: Handle,
}

impl Drop for InstanceGuard {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.handle);
        }
    }
}

pub fn acquire_single_instance(name: &str) -> Option<InstanceGuard> {
    let wide = to_wide(name);
    let handle = unsafe { CreateMutexW(std::ptr::null_mut(), 1, wide.as_ptr()) };
    if handle.is_null() {
        return None;
    }
    if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
        unsafe {
            CloseHandle(handle);
        }
        return None;
    }
    Some(InstanceGuard { handle })
}

pub fn require_windows() -> Result<()> {
    if cfg!(windows) {
        Ok(())
    } else {
        Err(anyhow!("sentinel-key only supports Windows"))
    }
}
