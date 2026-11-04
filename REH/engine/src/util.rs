use serde::Serialize;
use std::path::{Path, PathBuf};

pub type R<T = ()> = Result<T, Box<dyn std::error::Error>>;

pub fn bail(msg: impl std::fmt::Display) -> Box<dyn std::error::Error> {
    msg.to_string().into()
}

/// Rational fps helper. fps is stored as [num, den] in every contract file.
#[derive(Clone, Copy, Debug)]
pub struct Fps(pub u32, pub u32);

impl Fps {
    pub fn as_f64(self) -> f64 {
        self.0 as f64 / self.1 as f64
    }
    pub fn t_of(self, n: u64) -> f64 {
        n as f64 * self.1 as f64 / self.0 as f64
    }
    pub fn n_of(self, t: f64) -> u64 {
        let n = t * self.0 as f64 / self.1 as f64;
        (n.round().max(0.0)) as u64
    }
    pub fn pair(self) -> [u32; 2] {
        [self.0, self.1]
    }
}

/// Seconds -> "hhmmss" (6+ digits, total hours, may exceed 23).
pub fn hhmmss(t: f64) -> String {
    let s = t.max(0.0).floor() as u64;
    let h = s / 3600;
    let m = (s % 3600) / 60;
    let sec = s % 60;
    format!("{h:02}{m:02}{sec:02}")
}

/// Accepts "hh:mm:ss(.frac)", "mm:ss(.frac)" or plain seconds.
pub fn parse_time(s: &str) -> R<f64> {
    let s = s.trim();
    if s.is_empty() {
        return Err(bail("empty time"));
    }
    let parts: Vec<&str> = s.split(':').collect();
    if parts.len() > 3 {
        return Err(bail(format!("bad time {s:?}")));
    }
    let mut acc = 0.0f64;
    for p in parts {
        let v: f64 = p
            .trim()
            .parse()
            .map_err(|_| format!("bad time component {p:?} in {s:?}"))?;
        acc = acc * 60.0 + v;
    }
    Ok(acc)
}

pub fn slugify(file_name: &str) -> String {
    let stem = Path::new(file_name)
        .file_stem()
        .map(|s| s.to_string_lossy().to_string())
        .unwrap_or_else(|| "video".into());
    let mut out = String::new();
    for c in stem.chars() {
        if c.is_ascii_alphanumeric() || c == '-' || c == '_' {
            out.push(c.to_ascii_lowercase());
        } else if c == ' ' || c == '.' {
            if !out.ends_with('-') {
                out.push('-');
            }
        }
    }
    let out = out.trim_matches('-').to_string();
    if out.is_empty() {
        "video".into()
    } else {
        out
    }
}

pub fn ensure_dir(p: &Path) -> std::io::Result<()> {
    std::fs::create_dir_all(p)
}

pub fn write_json<T: Serialize>(path: &Path, v: &T) -> R<()> {
    if let Some(parent) = path.parent() {
        ensure_dir(parent)?;
    }
    let s = serde_json::to_string_pretty(v)?;
    std::fs::write(path, s + "\n")?;
    Ok(())
}

pub fn read_json<T: serde::de::DeserializeOwned>(path: &Path) -> R<T> {
    let s = std::fs::read_to_string(path)?;
    Ok(serde_json::from_str(&s)?)
}

pub fn write_text(path: &Path, text: &str) -> R<()> {
    if let Some(parent) = path.parent() {
        ensure_dir(parent)?;
    }
    std::fs::write(path, text)?;
    Ok(())
}

pub fn exists(p: &Path) -> bool {
    std::fs::symlink_metadata(p).is_ok()
}

/// The work/<slug>/ directory accessor bundle.
pub struct Work {
    pub root: PathBuf,
}

impl Work {
    pub fn new(work_root: &Path, slug: &str) -> Self {
        Self {
            root: work_root.join(slug),
        }
    }
    pub fn j(&self, name: &str) -> PathBuf {
        self.root.join(name)
    }
    pub fn probe(&self) -> PathBuf {
        self.j("probe.json")
    }
    pub fn frames_dir(&self) -> PathBuf {
        self.j("frames")
    }
    pub fn pngall_dir(&self) -> PathBuf {
        self.j("frames.pngall")
    }
    pub fn frames_json(&self) -> PathBuf {
        self.j("frames.json")
    }
    pub fn motion_json(&self) -> PathBuf {
        self.j("motion.json")
    }
    pub fn keep_raw(&self) -> PathBuf {
        self.j("keep.raw.json")
    }
    pub fn scenes_json(&self) -> PathBuf {
        self.j("scenes.json")
    }
    pub fn cuts_json(&self) -> PathBuf {
        self.j("cuts.json")
    }
    pub fn waveform(&self) -> PathBuf {
        self.j("waveform.json")
    }
    pub fn scenes_root(&self) -> PathBuf {
        self.j("scenes")
    }
    pub fn scene_dir(&self, i: usize, t0: f64) -> PathBuf {
        self.scenes_root().join(format!("{i:02}-{}", hhmmss(t0)))
    }
    pub fn out_dir(&self) -> PathBuf {
        self.j("out")
    }
    pub fn audio_named(&self, ext: &str) -> PathBuf {
        self.root.join(format!("audio.{ext}"))
    }
    pub fn any_audio(&self) -> Option<PathBuf> {
        ["m4a", "mp3", "opus", "ogg", "flac", "wav", "mka"]
            .iter()
            .map(|e| self.audio_named(e))
            .find(|p| exists(p))
    }
}
