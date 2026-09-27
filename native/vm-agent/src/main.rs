mod packet;
mod relay;

fn main() {
    // stdin uses a blocking OS read internally. A dead SSH peer must not keep the
    // runtime waiting for an uncancellable stdin/DNS task during shutdown.
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .max_blocking_threads(6)
        .enable_all()
        .build()
        .unwrap();
    let result = runtime.block_on(relay::run());
    if let Err(error) = &result {
        eprintln!("UDP bridge: {error}");
    }
    std::process::exit(if result.is_ok() { 0 } else { 1 });
}
