pub mod verify;

pub const ABI_VERSION: u32 = 1;

#[cfg(test)]
mod tests {
    #[test]
    fn abi_version_is_one() {
        assert_eq!(crate::ABI_VERSION, 1);
    }
}
