mod analyze;
mod audio;
mod ff;
mod keep;
mod pipeline;
mod probe;
mod scenes;
mod util;

use clap::{Args, Parser, Subcommand};
use std::path::PathBuf;
use std::process::ExitCode;

#[derive(Parser)]
#[command(name = "reh", version, about = "Reverse Engineer HyperFrames: prune an mp4 into a deliberate frame library for a coding agent to re-author as HyperFrames html")]
struct Cli {
    #[command(subcommand)]
    cmd: Cmd,
}

#[derive(Subcommand)]
enum Cmd {
    /// Full pass 1: probe, audio split, frames dump, motion/cut inference, scene folders
    Run(RunArgs),
    /// Print probe.json for a video (or an existing work dir)
    Probe(ProbeArgs),
    /// Inspect / correct scene cut points
    #[command(subcommand)]
    Scenes(scenes::ScenesCmd),
    /// What exists in a work dir and what is still pending
    Status { work: PathBuf },
    /// Bun preview server: scrub the video, click to insert cuts (execs bun)
    Preview { work: PathBuf, #[arg(long)] bun: Option<String> },
    /// Per-scene model descriptions via the firebending httpd (execs bun)
    Infer {
        work: PathBuf,
        #[arg(long)]
        bun: Option<String>,
        /// Remaining flags forwarded verbatim (see infer/infer.ts: --daemon --rate --force --dry-run)
        #[arg(trailing_var_arg = true, allow_hyphen_values = true)]
        rest: Vec<String>,
    },
}

#[derive(Args)]
pub struct RunArgs {
    /// Input video (mp4/mkv/...)
    pub video: PathBuf,
    /// Work directory root (default: REH-adjacent work/ beside the engine, or --work)
    #[arg(long)]
    pub work: Option<PathBuf>,
    /// Explicit slug for work/<slug>/
    #[arg(long)]
    pub slug: Option<String>,
    /// Analysis long edge for the gray stream (default 640)
    #[arg(long, default_value_t = 640)]
    pub scale: u32,
    /// JPEG quality for the full frames/ dump, ffmpeg q:v (2 best .. 31 worst; 3 ≈ q90)
    #[arg(long, default_value_t = 3)]
    pub jpeg_q: u32,
    /// Additionally dump every frame lossless (frames.pngall/)
    #[arg(long)]
    pub png_all: bool,
    /// HW decode: auto|cuda|vaapi|qsv|none (default auto)
    #[arg(long, default_value = "auto")]
    pub hw: String,
    /// Skip beat/onset hint extraction
    #[arg(long)]
    pub no_beats: bool,
    /// Redo stages even if outputs already exist
    #[arg(long)]
    pub force: bool,
}

#[derive(Args)]
struct ProbeArgs {
    /// Video file or work dir containing probe.json
    target: PathBuf,
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    let result: Result<(), Box<dyn std::error::Error>> = match cli.cmd {
        Cmd::Run(a) => pipeline::run(a),
        Cmd::Probe(a) => probe::cli(&a.target),
        Cmd::Scenes(c) => scenes::dispatch(c),
        Cmd::Status { work } => pipeline::status(&work),
        Cmd::Preview { work, bun } => pipeline::exec_bun(&bun, "preview/server.ts", &work, &[]),
        Cmd::Infer { work, bun, rest } => pipeline::exec_bun(&bun, "infer/infer.ts", &work, &rest),
    };
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("reh: {e}");
            ExitCode::FAILURE
        }
    }
}
