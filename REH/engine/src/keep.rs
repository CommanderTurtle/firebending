use crate::ff::{ffmpeg, run};
use crate::util::{ensure_dir, R};
use std::path::Path;

const CHUNK: usize = 250;

/// Extracts `frames` (frame numbers, 0-based) as lossless PNGs from the ORIGINAL
/// video via one ffmpeg select pass per CHUNK frames. Files land as
/// `<out>/NNNN.png` in ascending frame order matching the slice they came from.
pub fn extract(video: &str, frames: &[u64], out: &Path, hwaccel: &str) -> R<()> {
    ensure_dir(out)?;
    if frames.is_empty() {
        return Ok(());
    }
    let mut produced = 0usize;
    for (ci, chunk) in frames.chunks(CHUNK).enumerate() {
        let mut expr = String::with_capacity(chunk.len() * 16);
        for (k, f) in chunk.iter().enumerate() {
            if k > 0 {
                expr.push('+');
            }
            expr.push_str(&format!("eq(n\\,{f})"));
        }
        let vf = format!("select={expr}");
        let start = (ci * CHUNK).to_string();
        let mut cmd = ffmpeg(
            hwaccel,
            &[
                "-i",
                video,
                "-map",
                "0:v:0",
                "-an",
                "-vf",
                &vf,
                "-fps_mode",
                "passthrough",
                "-c:v",
                "png",
                "-y",
                "-start_number",
                &start,
            ],
        );
        cmd.arg(out.join("%04d.png"));
        run(cmd, "keep-frame extraction")?;
        produced += chunk.len();
    }
    // sanity: ffmpeg may emit fewer when frames >= stream length; caller tolerates misses
    let _ = produced;
    Ok(())
}
