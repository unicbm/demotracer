/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use sha2::{Digest, Sha256};
use std::fmt::Write;

pub const DEMO_ID_HASH_LEN: usize = 12;

pub fn sha256_hex(bytes: &[u8]) -> String {
    hex_encode(&Sha256::digest(bytes))
}

pub fn hex_encode(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        let _ = write!(&mut out, "{byte:02x}");
    }
    out
}

pub fn demo_id(stem: &str, demo_sha256: &str) -> String {
    format!(
        "{}-{}",
        slugify_demo_stem(stem),
        short_demo_hash(demo_sha256)
    )
}

fn short_demo_hash(demo_sha256: &str) -> String {
    demo_sha256.chars().take(DEMO_ID_HASH_LEN).collect()
}

fn slugify_demo_stem(value: &str) -> String {
    let mut out = String::new();
    for ch in value.chars() {
        if ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' {
            out.push(ch);
        } else if ch.is_whitespace() {
            out.push('_');
        }
    }
    if out.is_empty() {
        "demo".to_string()
    } else {
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn content_hash_changes_demo_id_for_same_stem() {
        let first = demo_id("match", &sha256_hex(b"first demo"));
        let second = demo_id("match", &sha256_hex(b"second demo"));

        assert_ne!(first, second);
    }

    #[test]
    fn demo_id_slugifies_stem_and_uses_hash12() {
        let hash = sha256_hex(b"demo bytes");
        let id = demo_id("Spirit vs Falcons m2 Mirage!", &hash);

        assert_eq!(id, format!("Spirit_vs_Falcons_m2_Mirage-{}", &hash[..12]));
    }
}
