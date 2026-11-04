use crate::ff::{ffprobe, run};
use crate::util::{bail, exists, read_json, write_json, Fps, Work, R};
use serde::{Deserialize, Serialize};
use std::path::Path;

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct Probe {
    pub path: String,
    pub vcodec: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub acodec: Option<String>,
    pub width: u32,
    pub height: u32,
    pub fps: [u32; 2],
    pub duration_s: f64,
    pub nb_frames: u64,
}

impl Probe {
    pub fn fps(&self) -> Fps {
        Fps(self.fps[0].max(1), self.fps[1].max(1))
    }
}

#[derive(Deserialize)]
struct FfOut {
    streams: Vec<Stream>,
    format: Format,
}

#[derive(Deserialize)]
struct Stream {
    #[serde(rename = "codec_type")]
    codec_type: String,
    #[serde(rename = "codec_name", default)]
    codec_name: Option<String>,
    width: Option<u64>,
    height: Option<u64>,
    #[serde(rename = "avg_frame_rate", default)]
    avg_frame_rate: Option<String>,
    nb_frames: Option<u64>,
    duration: Option<String>,
}

#[derive(Deserialize)]
struct Format {
    duration: Option<String>,
}

fn parse_ratio(s: &str) -> Option<(u64, u64)> {
    let (a, b) = s.trim().split_once('/')?;
    let n: u64 = a.parse().ok()?;
    let d: u64 = b.parse().ok()?;
    if n == 0 || d == 0 {
        None
    } else {
        Some((n, d))
    }
}

pub fn probe(video: &Path) -> R<Probe> {
    let out = run(
        ffprobe(&[
            "-of",
            "json",
            "-show_streams",
            "-show_format",
            video.to_str().ok_or("non-utf8 video path")?,
        ]),
        "ffprobe",
    )?;
    let parsed: FfOut = serde_json::from_slice(&out.stdout)?;
    let v = parsed
        .streams
        .iter()
        .find(|s| s.codec_type == "video")
        .ok_or("no video stream")?;
    let a = parsed
        .streams
        .iter()
        .find(|s| s.codec_type == "audio" && s.codec_name.as_deref() != Some("unknown"));
    let width = v.width.ok_or("stream without width")? as u32;
    let height = v.height.ok_or("stream without height")? as u32;
    let fps = v
        .avg_frame_rate
        .as_deref()
        .and_then(parse_ratio)
        .ok_or("unparsable avg_frame_rate")?;
    let dur = v
        .duration
        .as_deref()
        .and_then(|d| d.parse::<f64>().ok())
        .or_else(|| parsed.format.duration.as_deref().and_then(|d| d.parse::<f64>().ok()))
        .ok_or("no duration")?;
    let nb_frames = match v.nb_frames {
        Some(n) if n > 0 => n,
        _ => {
            // count pass fallback (accurate, one decode)
            let cout = run(
                ffprobe(&[
                    "-select_streams",
                    "v:0",
                    "-count_frames",
                    "-show_entries",
                    "stream=nb_read_frames",
                    "-of",
                    "csv=p=0",
                    video.to_str().ok_or("non-utf8 video path")?,
                ]),
                "ffprobe count",
            )?;
            let s = String::from_utf8_lossy(&cout.stdout).trim().to_string();
            s.parse()
                .map_err(|_| format!("frame count produced {s:?}"))?
        }
    };
    Ok(Probe {
        path: video.to_string_lossy().into_owned(),
        vcodec: v.codec_name.clone().unwrap_or_else(|| "unknown".into()),
        acodec: a.and_then(|s| s.codec_name.clone()),
        width,
        height,
        fps: [fps.0 as u32, fps.1 as u32],
        duration_s: (dur * 1e6).round() / 1e6,
        nb_frames,
    })
}

pub fn cli(target: &Path) -> R<()> {
    if target.is_dir() {
        let w = Work {
            root: target.to_path_buf(),
        };
        if !exists(&w.probe()) {
            return Err(bail(format!(
                "{} has no probe.json (run `reh run` first)",
                w.root.display()
            )));
        }
        let p: Probe = read_json(&w.probe())?;
        println!("{}", serde_json::to_string_pretty(&p)?);
        return Ok(());
    }
    let p = probe(target)?;
    println!("{}", serde_json::to_string_pretty(&p)?);
    Ok(())
}

pub fn save(w: &Work, p: &Probe) -> R<()> {
    write_json(&w.probe(), p)
}

pub fn load(w: &Work) -> R<Probe> {
    read_json(&w.probe())
}
