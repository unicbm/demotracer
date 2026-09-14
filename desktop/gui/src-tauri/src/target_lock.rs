/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

#[derive(Debug)]
pub(crate) struct TargetFileLock {
    path: PathBuf,
    file: Option<fs::File>,
}

impl TargetFileLock {
    pub(crate) fn acquire(path: &Path) -> io::Result<Self> {
        let mut options = fs::OpenOptions::new();
        options.read(true).write(true);
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            options.create(true).truncate(false).share_mode(0);
        }

        #[cfg(not(windows))]
        {
            options.create_new(true);
        }
        let file = options.open(path).map_err(|error| {
            #[cfg(windows)]
            let contended = matches!(error.raw_os_error(), Some(32) | Some(33));
            #[cfg(not(windows))]
            let contended = error.kind() == io::ErrorKind::AlreadyExists;
            if contended {
                io::Error::new(io::ErrorKind::WouldBlock, error)
            } else {
                error
            }
        })?;
        Ok(Self {
            path: path.to_path_buf(),
            file: Some(file),
        })
    }
}

impl Drop for TargetFileLock {
    fn drop(&mut self) {
        drop(self.file.take());
        let _ = fs::remove_file(&self.path);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn target_lock_excludes_a_second_writer_and_releases_on_drop() {
        let root = std::env::temp_dir().join(format!(
            "demotracer-target-lock-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("target.lock");

        let first = TargetFileLock::acquire(&path).unwrap();
        assert_eq!(
            TargetFileLock::acquire(&path).unwrap_err().kind(),
            io::ErrorKind::WouldBlock
        );
        drop(first);
        drop(TargetFileLock::acquire(&path).unwrap());

        fs::remove_dir_all(root).unwrap();
    }
}
