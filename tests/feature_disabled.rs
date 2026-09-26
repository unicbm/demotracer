/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

#![cfg(not(feature = "demoparser"))]

use cs2_demotracer::demo_reader::{
    read_demo_bytes_with_options_and_cancel, read_demo_with_options_and_cancel,
    read_loaded_demo_with_options_and_cancel, LoadedDemoInput, ReadDemoOptions,
};
use cs2_demotracer::Error;
use std::path::Path;
use std::sync::atomic::AtomicBool;

#[test]
fn cancellation_entrypoints_remain_available_without_parser_or_input_io() {
    let cancelled = AtomicBool::new(true);
    let input = LoadedDemoInput {
        bytes: Vec::new(),
        stem: "missing".into(),
        display_path: "missing.dem".into(),
        compressed: false,
    };
    for result in [
        read_demo_with_options_and_cancel(
            Path::new("missing.dem"),
            ReadDemoOptions::default(),
            Some(&cancelled),
        ),
        read_demo_bytes_with_options_and_cancel(
            &[],
            "missing",
            "missing.dem",
            ReadDemoOptions::default(),
            Some(&cancelled),
        ),
        read_loaded_demo_with_options_and_cancel(
            &input,
            ReadDemoOptions::default(),
            Some(&cancelled),
        ),
    ] {
        assert!(matches!(result, Err(Error::FeatureDisabled("demoparser"))));
    }
}
