// Process-wide allocator for the parser's allocation-heavy per-tick workload.
#[global_allocator]
static GLOBAL: mimalloc::MiMalloc = mimalloc::MiMalloc;

#[cfg(test)]
pub mod e2e_test;
mod demo_network_handle;
pub mod first_pass;
pub mod maps;
pub mod parse_demo;
mod profile;
pub mod second_pass;
