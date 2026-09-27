mod control;
mod deployment;
mod forward;
mod ssh;

fn main() {
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(4)
        .max_blocking_threads(4)
        .enable_all()
        .build()
        .unwrap();
    let result = runtime.block_on(control::run());
    // Never print configuration, credentials, or a remote command to stderr.
    if let Err(error) = &result {
        eprintln!("Relay core: {error}");
    }
    // Tokio stdin and a stalled system resolver are not cancellable OS calls.
    // Explicit process exit is the final ownership boundary for those threads.
    std::process::exit(if result.is_ok() { 0 } else { 1 });
}
