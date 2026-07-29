mod analysis;
pub mod browser_analysis;
pub mod demo_id;
pub mod demo_reader;
pub mod demo_series;
pub mod export;
mod inspect_link;
pub mod model;
pub mod quality;
pub mod rec_writer;
mod replay;
pub mod synthesis;
pub mod validate;
pub mod voice_export;

pub mod dtr {
    pub use crate::model::{
        Cs2Rec, Cs2RecHeader, MovementSnapshot, ReplayProjectile, ReplayTick, SubtickMove,
        DTR_FORMAT_VERSION,
    };
    pub use crate::rec_writer::{
        read_rec, read_rec_file, read_rec_file_with_limits, read_rec_with_limits, write_rec_file,
        DtrReadLimits,
    };
}

pub type Result<T> = std::result::Result<T, Error>;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("I/O error for {path}: {source}")]
    Io {
        path: String,
        #[source]
        source: std::io::Error,
    },
    #[error("invalid .dtr: {0}")]
    InvalidRec(String),
    #[error("invalid demo data: {0}")]
    InvalidDemo(String),
    #[error("demoparser error: {0}")]
    Parser(String),
    #[error("failed to decompress zstd demo {path}: {source}")]
    ZstdDemo {
        path: String,
        #[source]
        source: std::io::Error,
    },
    #[error("logical demo content exceeds the safety limit of {limit_bytes} bytes: {path}")]
    DecompressedDemoTooLarge { path: String, limit_bytes: u64 },
    #[error(
        "cannot allocate memory while loading demo {path} after {decoded_bytes} decoded bytes: {source}"
    )]
    DemoAllocation {
        path: String,
        decoded_bytes: u64,
        #[source]
        source: std::collections::TryReserveError,
    },
    #[error("JSON error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("this build was compiled without the `{0}` feature")]
    FeatureDisabled(&'static str),
}

pub fn io_error(path: impl AsRef<std::path::Path>, source: std::io::Error) -> Error {
    Error::Io {
        path: path.as_ref().display().to_string(),
        source,
    }
}
