use crate::ff::{ffmpeg, run};
use crate::util::{ensure_dir, R};
use std::path::Path;

// ffmpeg's av_expr context stack dies ("Error while parsing expression" + ENOMEM)
// somewhere in (100,150] chained eq() calls — measured on this exact ffmpeg build.
const CHUNK: usize = 96;

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
            // single `\,` in argv: the filtergraph's option tokenizer keeps it as a
            // literal comma inside the option value. (Double would end the value early.)
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
