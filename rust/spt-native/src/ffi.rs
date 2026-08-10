use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;

use crate::runtime::runtime;
use crate::verify;

pub const STATUS_OK: i32 = 0;
pub const STATUS_BAD_ARGS: i32 = 1;
pub const STATUS_PANIC: i32 = 2;

#[unsafe(no_mangle)]
pub extern "C" fn spt_native_abi_version() -> u32 {
    crate::ABI_VERSION
}

/// # Safety
/// `dir_ptr` must point to `dir_len` readable bytes of UTF-8; `out_ptr` and `out_len`
/// must be valid for writes. The returned buffer must be released with `spt_buf_free`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn spt_verify_database(
    dir_ptr: *const u8,
    dir_len: usize,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    if dir_ptr.is_null() || out_ptr.is_null() || out_len.is_null() {
        return STATUS_BAD_ARGS;
    }
    let dir_bytes = unsafe { std::slice::from_raw_parts(dir_ptr, dir_len) };
    let Ok(dir) = std::str::from_utf8(dir_bytes) else {
        return STATUS_BAD_ARGS;
    };
    let dir = PathBuf::from(dir);

    let result = catch_unwind(AssertUnwindSafe(|| {
        let report = runtime().block_on(verify::verify(dir));
        serde_json::to_vec(&report).expect("VerifyReport serialization cannot fail")
    }));

    match result {
        Ok(json) => {
            let boxed = json.into_boxed_slice();
            let len = boxed.len();
            unsafe {
                *out_ptr = Box::into_raw(boxed) as *mut u8;
                *out_len = len;
            }
            STATUS_OK
        }
        Err(_) => STATUS_PANIC,
    }
}

/// # Safety
/// `ptr` and `len` must come from a successful `spt_verify_database` call and be freed at most once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn spt_buf_free(ptr: *mut u8, len: usize) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len)));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use tempfile::TempDir;

    fn call_verify(dir: &str) -> (i32, Option<serde_json::Value>) {
        let mut out_ptr: *mut u8 = std::ptr::null_mut();
        let mut out_len: usize = 0;
        let status =
            unsafe { spt_verify_database(dir.as_ptr(), dir.len(), &mut out_ptr, &mut out_len) };
        if status != STATUS_OK {
            return (status, None);
        }
        let json = unsafe { std::slice::from_raw_parts(out_ptr, out_len) }.to_vec();
        unsafe { spt_buf_free(out_ptr, out_len) };
        (status, Some(serde_json::from_slice(&json).unwrap()))
    }

    #[test]
    fn abi_version_export_matches_crate_const() {
        assert_eq!(spt_native_abi_version(), crate::ABI_VERSION);
    }

    #[test]
    fn verify_roundtrips_report_json() {
        let dir = TempDir::new().unwrap();
        fs::create_dir_all(dir.path().join("database")).unwrap();
        let (status, report) = call_verify(dir.path().to_str().unwrap());
        assert_eq!(status, STATUS_OK);
        let report = report.unwrap();
        assert_eq!(report["ok"], false);
        assert_eq!(report["failures"][0]["path"], "checks.dat");
    }

    #[test]
    fn null_arguments_return_bad_args() {
        let status = unsafe {
            spt_verify_database(
                std::ptr::null(),
                0,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            )
        };
        assert_eq!(status, STATUS_BAD_ARGS);
    }

    #[test]
    fn invalid_utf8_returns_bad_args() {
        let bad = [0xFFu8, 0xFE];
        let mut out_ptr: *mut u8 = std::ptr::null_mut();
        let mut out_len: usize = 0;
        let status =
            unsafe { spt_verify_database(bad.as_ptr(), bad.len(), &mut out_ptr, &mut out_len) };
        assert_eq!(status, STATUS_BAD_ARGS);
    }

    #[test]
    fn freeing_null_is_a_no_op() {
        unsafe { spt_buf_free(std::ptr::null_mut(), 0) };
    }
}
