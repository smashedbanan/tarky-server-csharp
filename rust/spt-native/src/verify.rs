use std::collections::HashMap;
use std::path::{Path, PathBuf};

use serde::Deserialize;

#[derive(Deserialize)]
struct ManifestEntry {
    #[serde(rename = "Path")]
    path: String,
    #[serde(rename = "Hash")]
    hash: String,
}

pub fn parse_manifest(raw: &[u8]) -> Result<HashMap<String, String>, String> {
    use base64::Engine;

    let text = std::str::from_utf8(raw).map_err(|e| format!("checks.dat is not UTF-8: {e}"))?;
    let json = base64::engine::general_purpose::STANDARD
        .decode(text.trim())
        .map_err(|e| format!("checks.dat is not valid base64: {e}"))?;
    let entries: Vec<ManifestEntry> =
        serde_json::from_slice(&json).map_err(|e| format!("checks.dat JSON is invalid: {e}"))?;
    Ok(entries.into_iter().map(|e| (e.path, e.hash)).collect())
}

const IGNORED_FILE_NAMES: [&str; 3] = ["bearsuits.json", "usecsuits.json", "archivedquests.json"];
const IGNORED_DIR_KEYS: [&str; 2] = ["database/locales/server", "database/locales/web"];

fn relative_key(spt_data: &Path, path: &Path) -> Option<String> {
    let rel = path.strip_prefix(spt_data).ok()?;
    Some(rel.to_string_lossy().replace('\\', "/"))
}

pub fn collect_files(spt_data: &Path) -> Vec<(PathBuf, String)> {
    let database_dir = spt_data.join("database");
    let mut files = Vec::new();

    let walker = walkdir::WalkDir::new(&database_dir)
        .into_iter()
        .filter_entry(|entry| {
            if !entry.file_type().is_dir() {
                return true;
            }
            match relative_key(spt_data, entry.path()) {
                Some(key) => !IGNORED_DIR_KEYS.contains(&key.as_str()),
                None => true,
            }
        });

    for entry in walker.flatten() {
        if !entry.file_type().is_file() {
            continue;
        }
        let path = entry.path();
        if path.extension().and_then(|e| e.to_str()) != Some("json") {
            continue;
        }
        let name = entry.file_name().to_string_lossy().to_lowercase();
        if IGNORED_FILE_NAMES.contains(&name.as_str()) {
            continue;
        }
        if let Some(key) = relative_key(spt_data, path) {
            files.push((path.to_path_buf(), key));
        }
    }

    files
}

#[cfg(test)]
mod tests {
    use super::*;
    use base64::Engine;

    fn encode_manifest(json: &str) -> Vec<u8> {
        base64::engine::general_purpose::STANDARD
            .encode(json.as_bytes())
            .into_bytes()
    }

    #[test]
    fn parses_valid_manifest() {
        let raw = encode_manifest(
            r#"[{"Path":"database/templates/items.json","Hash":"06B05AB6733A618578AF5F94892F3950"}]"#,
        );
        let map = parse_manifest(&raw).unwrap();
        assert_eq!(
            map.get("database/templates/items.json").map(String::as_str),
            Some("06B05AB6733A618578AF5F94892F3950")
        );
    }

    #[test]
    fn rejects_invalid_base64() {
        assert!(parse_manifest(b"!!! not base64 !!!").is_err());
    }

    #[test]
    fn rejects_invalid_json() {
        let raw = encode_manifest("{ definitely not an array");
        assert!(parse_manifest(&raw).is_err());
    }

    #[test]
    fn tolerates_surrounding_whitespace() {
        let mut raw = b"\n  ".to_vec();
        raw.extend(encode_manifest(r#"[{"Path":"a.json","Hash":"AA"}]"#));
        raw.extend(b"  \n");
        assert!(parse_manifest(&raw).is_ok());
    }

    use std::fs;
    use tempfile::TempDir;

    fn touch(root: &Path, rel: &str) {
        let path = root.join(rel);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, b"{}").unwrap();
    }

    fn keys(spt_data: &Path) -> Vec<String> {
        let mut keys: Vec<String> = collect_files(spt_data)
            .into_iter()
            .map(|(_, k)| k)
            .collect();
        keys.sort();
        keys
    }

    #[test]
    fn collects_nested_json_files_only() {
        let dir = TempDir::new().unwrap();
        touch(dir.path(), "database/globals.json");
        touch(dir.path(), "database/templates/items.json");
        touch(dir.path(), "database/readme.txt");
        touch(dir.path(), "configs/core.json");
        assert_eq!(
            keys(dir.path()),
            vec![
                "database/globals.json".to_string(),
                "database/templates/items.json".to_string()
            ]
        );
    }

    #[test]
    fn excludes_ignored_filenames_case_insensitively() {
        let dir = TempDir::new().unwrap();
        touch(dir.path(), "database/BearSuits.json");
        touch(dir.path(), "database/usecsuits.json");
        touch(dir.path(), "database/ArchivedQuests.json");
        touch(dir.path(), "database/kept.json");
        assert_eq!(keys(dir.path()), vec!["database/kept.json".to_string()]);
    }

    #[test]
    fn excludes_ignored_locale_directories() {
        let dir = TempDir::new().unwrap();
        touch(dir.path(), "database/locales/server/en.json");
        touch(dir.path(), "database/locales/web/en.json");
        touch(dir.path(), "database/locales/global/en.json");
        assert_eq!(
            keys(dir.path()),
            vec!["database/locales/global/en.json".to_string()]
        );
    }

    #[test]
    fn uppercase_json_extension_is_not_collected() {
        let dir = TempDir::new().unwrap();
        touch(dir.path(), "database/loud.JSON");
        touch(dir.path(), "database/quiet.json");
        assert_eq!(keys(dir.path()), vec!["database/quiet.json".to_string()]);
    }
}
