use crate::analyze;
use crate::audio;
use crate::ff;
use crate::probe;
use crate::scenes;
use crate::util::{bail, ensure_dir, exists, slugify, Work, R};
use std::path::{Path, PathBuf};
use std::process::Command;

use crate::RunArgs;

fn video_path(v: &Path) -> R<PathBuf> {
    let p = if v.is_absolute() {
        v.to_path_buf()
    } else {
        std::env::current_dir()?.join(v)
    };
    if !p.is_file() {
        return Err(bail(format!("no such video: {}", p.display())));
    }
    Ok(p)
}

/// REH root = dir containing engine/, preview/, infer/ (env REH_ROOT wins,
/// then exe ancestors, then cwd ancestors).
fn reh_root() -> PathBuf {
    if let Ok(r) = std::env::var("REH_ROOT") {
        let p = PathBuf::from(r);
        if p.is_dir() {
            return p;
        }
    }
    let anchor = std::env::current_exe()
        .ok()
        .and_then(|e| e.parent().map(|p| p.to_path_buf()))
        .unwrap_or_else(|| std::env::current_dir().unwrap_or_default());
    let has_parts = |d: &Path| d.join("preview").join("server.ts").is_file();
    let mut cur = Some(anchor.as_path());
    while let Some(d) = cur {
        if has_parts(d) {
            return d.to_path_buf();
        }
        cur = d.parent();
    }
    let mut cur = std::env::current_dir().ok();
    while let Some(d) = cur.as_deref() {
        if has_parts(d) {
            return d.to_path_buf();
        }
        cur = d.parent().map(|p| p.to_path_buf());
    }
    PathBuf::from(".")
}

fn default_work_root() -> PathBuf {
    if let Ok(r) = std::env::var("REH_WORK") {
        return PathBuf::from(r);
    }
    reh_root().join("work")
}

pub fn run(a: RunArgs) -> R<()> {
    let video = video_path(&a.video)?;
    let root = a.work.clone().unwrap_or_else(default_work_root);
    ensure_dir(&root)?;
    let slug = a.slug.clone().unwrap_or_else(|| slugify(&video.file_name().unwrap_or_default().to_string_lossy()));
    let w = Work::new(&root, &slug);

    if exists(&w.probe()) {
        let old: probe::Probe = crate::util::read_json(&w.probe()).unwrap_or_default_probe();
        if old.path != video.to_string_lossy() && !a.force {
            return Err(bail(format!(
                "work/{} belongs to {}. Use --slug <name> or --force to take over.",
                slug, old.path
            )));
        }
    }
    ensure_dir(&w.root)?;
    eprintln!("reh: {} → {}", video.display(), w.root.display());

    let p = probe::probe(&video)?;
    probe::save(&w, &p)?;
    println!(
        "probe: {}x{} {}/{} fps, {} frames, {:.3}s, v={} a={}",
        p.width,
        p.height,
        p.fps[0],
        p.fps[1],
        p.nb_frames,
        p.duration_s,
        p.vcodec,
        p.acodec.as_deref().unwrap_or("none")
    );

    let audio_ext = audio::remux(&w, &p, a.force)?;
    let wf = audio::build(&w, &p, a.no_beats, a.force)?;
    println!(
        "audio: {} · {} peaks @{}Hz, {} silence runs, {} beat hints",
        audio_ext
            .as_deref()
            .map(|e| format!("lossless audio.{e}"))
            .unwrap_or_else(|| "no audio stream".into()),
        wf.peaks.len(),
        wf.rate,
        wf.silence.len(),
        wf.beats.len()
    );

    let an = if exists(&w.frames_json()) && !a.force {
        eprintln!("analyze: frames.json exists, skipping (use --force to redo)");
        let m: scenes::MotionFile = crate::util::read_json(&w.motion_json())?;
        analyze::Output {
            nb_frames: m.intervals.iter().map(|i| i.f1).max().unwrap_or(0),
            motion_intervals: m.intervals.iter().filter(|i| i.kind == "motion").count(),
        }
    } else {
        analyze::run(&w, &p, a.scale, a.jpeg_q, a.png_all, &a.hw, true)?
    };
    println!(
        "frames: {} analyzed at long-edge {}, {} motion intervals",
        an.nb_frames, a.scale, an.motion_intervals
    );

    let sf = scenes::build_scenes(&w)?;
    let n = scenes::export(&w)?;
    println!(
        "scenes: {} inferred boundaries → {} folders with keep-frames + notes.md",
        sf.cuts.iter().filter(|c| c.source == "infer").count(),
        n
    );
    println!(
        "\nnext:\n  reh scenes review {}\n  (correct cuts or) reh scenes apply {} --lgtm\n  bun {}/../infer/infer.ts {}   # or reh infer {}",
        w.root.display(),
        w.root.display(),
        reh_root().display(),
        w.root.display(),
        w.root.display()
    );
    Ok(())
}

pub fn status(work: &Path) -> R<()> {
    let w = Work {
        root: work.to_path_buf(),
    };
    if !exists(&w.root) {
        return Err(bail(format!("no such work dir: {}", w.root.display())));
    }
    let line = |label: &str, ok: bool, extra: &str| {
        println!(" [{}] {label}{}", if ok { "\x1b[32m✓\x1b[0m" } else { "\x1b[33m·\x1b[0m" }, extra)
    };
    line("probe.json", exists(&w.probe()), "");
    if let Some(a) = w.any_audio() {
        line(
            "audio (lossless)",
            true,
            &format!(" — {}", a.file_name().unwrap_or_default().to_string_lossy()),
        );
    } else {
        line("audio (lossless)", false, "");
    }
    line("waveform.json", exists(&w.waveform()), "");
    let nframes = std::fs::read_dir(w.frames_dir()).map(|d| d.count()).unwrap_or(0);
    line("frames/ dump", nframes > 0, &format!(" — {nframes} jpgs"));
    line("frames.json analysis", exists(&w.frames_json()), "");
    line("motion.json", exists(&w.motion_json()), "");
    line("scenes.json", exists(&w.scenes_json()), "");
    let scene_dirs: Vec<PathBuf> = std::fs::read_dir(w.scenes_root())
        .map(|rd| rd.flatten().map(|e| e.path()).filter(|p| p.is_dir()).collect())
        .unwrap_or_default();
    let descs = scene_dirs
        .iter()
        .filter(|d| exists(&d.join("description.md")))
        .count();
    let keeps = scene_dirs
        .iter()
        .map(|d| {
            std::fs::read_dir(d.join("keep"))
                .map(|k| k.count())
                .unwrap_or(0)
        })
        .sum::<usize>();
    line(
        "scenes/",
        !scene_dirs.is_empty(),
        &format!(" — {} folders, {keeps} keep PNGs, {descs}/{} described", scene_dirs.len(), scene_dirs.len().max(1)),
    );
    line("out/index.html", exists(&w.out_dir().join("index.html")), "");
    if !exists(&w.frames_json()) {
        println!("\nnext: reh run <video> --work {}", w.root.parent().unwrap_or(Path::new(".")).display());
    } else if scene_dirs.is_empty() {
        println!("\nnext: reh scenes export {}", w.root.display());
    } else if descs < scene_dirs.len() {
        println!(
            "\nnext: bun infer/infer.ts {}   # or: reh infer {}",
            w.root.display(),
            w.root.display()
        );
    } else {
        println!("\nnext: open a coding agent with REH/SKILL.md on {}", w.root.display());
    }
    Ok(())
}

/// Execs bun with a REH-relative script; never returns.
pub fn exec_bun(bun: &Option<String>, rel: &str, work: &Path, rest: &[String]) -> R<()> {
    let root = reh_root();
    let script = root.join(rel);
    if !script.is_file() {
        return Err(bail(format!(
            "{rel} not found under REH root {} (set REH_ROOT)",
            root.display()
        )));
    }
    let exe = bun.clone().unwrap_or_else(|| "bun".into());
    if !ff::exe_exists(&exe) {
        return Err(bail(format!(
            "{exe} not on PATH — bun is required for preview/infer (or pass --bun)"
        )));
    }
    let mut cmd = Command::new(&exe);
    cmd.arg(&script).arg(work);
    for r in rest {
        cmd.arg(r);
    }
    eprintln!("$ {} {} {}{}", exe, script.display(), work.display(), if rest.is_empty() { String::new() } else { format!(" {}", rest.join(" ")) });
    let code = cmd
        .status()
        .map_err(|e| format!("failed to exec {exe}: {e}"))?
        .code()
        .unwrap_or(1);
    std::process::exit(code);
}

trait DefaultProbe {
    fn unwrap_or_default_probe(self) -> probe::Probe;
}
impl DefaultProbe for Result<probe::Probe, Box<dyn std::error::Error>> {
    fn unwrap_or_default_probe(self) -> probe::Probe {
        self.unwrap_or(probe::Probe {
            path: String::new(),
            vcodec: String::new(),
            acodec: None,
            width: 0,
            height: 0,
            fps: [1, 1],
            duration_s: 0.0,
            nb_frames: 0,
        })
    }
}
