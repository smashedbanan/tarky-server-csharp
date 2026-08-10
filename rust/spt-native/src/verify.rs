use std::collections::HashMap;

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
}
