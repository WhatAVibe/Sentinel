mod agent;
mod app;
mod cli;
mod config;
mod drives;
mod guard;
mod marker;
mod ops;
mod platform;
mod startup;
mod state;

use std::process::ExitCode;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.first().map(String::as_str) {
        None | Some("gui") => app::run(),
        Some("--help" | "-h" | "help") => {
            print_help();
            Ok(())
        }
        Some("--version" | "-V" | "version") => {
            println!("sentinel-key {}", env!("CARGO_PKG_VERSION"));
            Ok(())
        }
        Some("list") => cli::list(&args[1..]),
        Some("enroll") => cli::enroll(&args[1..]),
        Some("forget") => cli::forget(&args[1..]),
        Some("status") => cli::status(&args[1..]),
        Some("probe") => cli::probe(&args[1..]),
        Some("guard" | "--guard") => guard::run(),
        Some("pause") => cli::pause(&args[1..]),
        Some("startup") => cli::startup(&args[1..]),
        Some("selftest") => cli::selftest(),
        Some(other) => {
            eprintln!("unknown command: {other}");
            print_help();
            Ok(())
        }
    };

    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("sentinel-key: {error:#}");
            ExitCode::FAILURE
        }
    }
}

fn print_help() {
    println!(
        "sentinel-key {}\n\
         Encrypted USB presence key for Sentinel.\n\n\
         Usage:\n\
           sentinel-key                  Open the management window\n\
           sentinel-key list [--json]    Show removable drives and key state\n\
           sentinel-key enroll E:        Prepare a drive and bind it to this PC\n\
           sentinel-key forget E:        Remove the marker and binding\n\
           sentinel-key status [--json]  Show enrolled keys and presence\n\
           sentinel-key probe [count]    Time full removable-drive scans\n\
           sentinel-key guard            Run the background presence watcher\n\
           sentinel-key pause 10         Pause enforcement for N minutes\n\
           sentinel-key startup on|off   Start the guard at Windows login\n\
           sentinel-key selftest         Check crypto, DPAPI and agent access",
        env!("CARGO_PKG_VERSION")
    );
}
