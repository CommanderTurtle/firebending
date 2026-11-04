use crate::analyze::Interval;
use crate::audio::Waveform;
use crate::keep;
use crate::probe::{self, Probe};
use crate::util::{bail, ensure_dir, exists, hhmmss, parse_time, read_json, write_json, write_text, Fps, Work, R};
use clap::Subcommand;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Subcommand)]
pub enum ScenesCmd {
    /// Print inferred cuts, audio hints and current scene table
    Review { work: PathBuf },
    /// Accept inferred cuts (--lgtm) or supply user cut points
    Apply(ApplyArgs),
    /// Rebuild scene folders/meta/notes from scenes.json (descriptions preserved)
    Export { work: PathBuf },
}

#[derive(clap::Args)]
pub struct ApplyArgs {
    pub work: PathBuf,
    /// Accept the inferred cut list as-is
    #[arg(long)]
    pub lgtm: bool,
    /// Comma-separated cut points: "00:00:00, 00:00:12, 1:03.5"
    #[arg(long)]
    pub cuts: Option<String>,
    /// cuts.json-shaped file with user cuts
    #[arg(long)]
    pub file: Option<PathBuf>,
}

// ---------- contract types ----------

#[derive(Deserialize)]
pub struct MotionFile {
    pub intervals: Vec<Interval>,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct CutRow {
    pub f: u64,
    pub t: f64,
    pub source: String, // infer | user | audio
    pub kind: String,   // hard | dissolve | manual | beat
    pub conf: f32,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct SceneRow {
    pub i: usize,
    pub f0: u64,
    pub f1: u64,
    pub t0: f64,
    pub t1: f64,
    pub dir: String,
}

#[derive(Serialize, Deserialize)]
pub struct ScenesFile {
    pub slug: String,
    pub fps: [u32; 2],
    pub width: u32,
    pub height: u32,
    pub nb_frames: u64,
    pub inferred: Vec<[u64; 2]>,
    pub cuts: Vec<CutRow>,
    pub scenes: Vec<SceneRow>,
}

#[derive(Deserialize, Serialize)]
pub struct CutsFile {
    pub cuts: Vec<UserCut>,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub confirmed: bool,
}

#[derive(Deserialize, Serialize, Clone)]
pub struct UserCut {
    pub f: Option<u64>,
    pub t: f64,
}

#[derive(Deserialize)]
pub struct KeepRaw {
    pub scenes: Vec<KeepRawScene>,
}

#[derive(Deserialize)]
pub struct KeepRawScene {
    pub i: usize,
    pub keep: Vec<u64>,
}

#[derive(Serialize)]
pub struct Meta {
    pub i: usize,
    pub dir: String,
    pub f0: u64,
    pub f1: u64,
    pub t0: f64,
    pub t1: f64,
    pub r#in: Option<Boundary>,
    pub out: Option<Boundary>,
    pub motions: Vec<MotionRef>,
    pub audio_hints: AudioHints,
    pub keep_f: Vec<u64>,
}

#[derive(Serialize)]
pub struct Boundary {
    pub f: u64,
    pub kind: String,
}

#[derive(Serialize)]
pub struct MotionRef {
    pub kind: String,
    pub f0: u64,
    pub f1: u64,
    pub bbox: [u32; 4],
    pub peak_f: u64,
}

#[derive(Serialize)]
pub struct AudioHints {
    pub silence_in: Vec<[f64; 2]>,
    pub beats_in: Vec<f64>,
}

// ---------- build ----------

pub fn fps_of(p: &Probe) -> Fps {
    p.fps()
}

pub fn build_scenes(work: &Work) -> R<ScenesFile> {
    let probe: Probe = read_json(&work.probe())?;
    let motion: MotionFile = read_json(&work.motion_json())
        .map_err(|_| "motion.json missing — run `reh run` first")?;
    let fps = fps_of(&probe);
    let nb = probe.nb_frames;

    let mut infer_cuts: Vec<CutRow> = motion
        .intervals
        .iter()
        .filter(|i| i.kind == "hard" || i.kind == "dissolve")
        .map(|i| CutRow {
            f: i.f0,
            t: i.t0,
            source: "infer".into(),
            kind: i.kind.clone(),
            conf: 0.9,
        })
        .collect();
    infer_cuts.sort_by_key(|c| c.f);
    infer_cuts.dedup_by_key(|c| c.f);

    let wf: Waveform = read_json(&work.waveform()).unwrap_or(Waveform {
        rate: 50,
        peaks: vec![],
        silence: vec![],
        beats: vec![],
    });
    let audio_cuts: Vec<CutRow> = wf
        .beats
        .iter()
        .map(|b| CutRow {
            f: fps.n_of(b.t),
            t: b.t,
            source: "audio".into(),
            kind: "beat".into(),
            conf: b.strength as f32,
        })
        .collect();

    let inferred_ranges = ranges_from(&infer_cuts, nb, &fps);

    // user precedence: cuts.json user entries replace inferred boundaries
    let user: Vec<UserCut> = if exists(&work.cuts_json()) {
        read_json::<CutsFile>(&work.cuts_json())
            .map(|c| c.cuts)
            .unwrap_or_default()
    } else {
        Vec::new()
    };
    let min_gap = (0.0666 * fps.as_f64()).round().max(2.0) as u64;
    let mut user_frames: Vec<u64> = user
        .iter()
        .map(|c| c.f.unwrap_or_else(|| fps.n_of(c.t)))
        .filter(|f| *f > 0 && *f < nb)
        .collect();
    user_frames.sort_unstable();
    user_frames.dedup();
    user_frames = thin(user_frames, min_gap);

    let boundaries: Vec<u64> = if !user_frames.is_empty() {
        user_frames.clone()
    } else {
        infer_cuts.iter().map(|c| c.f).collect()
    };

    let mut cuts: Vec<CutRow> = Vec::new();
    if !user_frames.is_empty() {
        cuts.extend(user_frames.iter().map(|f| CutRow {
            f: *f,
            t: round6(fps.t_of(*f)),
            source: "user".into(),
            kind: "manual".into(),
            conf: 1.0,
        }));
    } else {
        cuts.extend(infer_cuts.clone());
    }
    cuts.extend(audio_cuts);
    cuts.sort_by_key(|c| (c.f, c.source != "user", c.source != "infer"));

    let scenes = table(&boundaries, nb, &fps, work, &probe);

    let sf = ScenesFile {
        slug: work.root.file_name().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default(),
        fps: fps.pair(),
        width: probe.width,
        height: probe.height,
        nb_frames: nb,
        inferred: inferred_ranges,
        cuts,
        scenes,
    };
    write_json(&work.scenes_json(), &sf)?;
    Ok(sf)
}

fn thin(mut v: Vec<u64>, gap: u64) -> Vec<u64> {
    let mut out: Vec<u64> = Vec::new();
    for f in v.drain(..) {
        match out.last() {
            Some(&last) if f.saturating_sub(last) < gap => {}
            _ => out.push(f),
        }
    }
    out
}

fn ranges_from(cuts: &[CutRow], nb: u64, fps: &Fps) -> Vec<[u64; 2]> {
    let bs: Vec<u64> = cuts.iter().map(|c| c.f).filter(|f| *f > 0 && *f < nb).collect();
    let _ = fps;
    let mut out = Vec::new();
    let mut start = 0u64;
    for b in &bs {
        out.push([start, b - 1]);
        start = *b;
    }
    if nb > 0 {
        out.push([start, nb - 1]);
    }
    out
}

fn table(boundaries: &[u64], nb: u64, fps: &Fps, work: &Work, _probe: &Probe) -> Vec<SceneRow> {
    let mut out = Vec::new();
    let starts = std::iter::once(0u64).chain(boundaries.iter().copied());
    let ends = boundaries
        .iter()
        .map(|b| b - 1)
        .chain(std::iter::once(nb.saturating_sub(1)));
    for (i, (f0, f1)) in starts.zip(ends).enumerate() {
        if f0 > f1 {
            continue;
        }
        let t0 = round6(fps.t_of(f0));
        out.push(SceneRow {
            i,
            f0,
            f1,
            t0,
            t1: round6(fps.t_of(f1)),
            dir: format!("{:02}-{}", i, hhmmss(t0)),
        });
        let _ = work;
    }
    out
}

fn round6(x: f64) -> f64 {
    (x * 1e6).round() / 1e6
}

// ---------- export ----------

pub fn export(work: &Work) -> R<usize> {
    let probe: Probe = read_json(&work.probe())?;
    let sf: ScenesFile = read_json(&work.scenes_json())
        .map_err(|_| "scenes.json missing — run `reh scenes` or `reh run` first")?;
    let motion: MotionFile = read_json(&work.motion_json()).unwrap_or(MotionFile {
        intervals: vec![],
    });
    let wf: Waveform = read_json(&work.waveform()).unwrap_or(Waveform {
        rate: 50,
        peaks: vec![],
        silence: vec![],
        beats: vec![],
    });
    let fps = Fps(sf.fps[0], sf.fps[1]);
    let keep_raw: Option<KeepRaw> = read_json(&work.keep_raw()).ok();
    let raw_matches = keep_raw
        .as_ref()
        .map(|k| k.scenes.len() == sf.scenes.len() && k.scenes.iter().zip(&sf.scenes).all(|(k, s)| k.i == s.i))
        .unwrap_or(false);

    // per-scene keep lists
    let mut lists: Vec<(usize, Vec<u64>)> = Vec::new();
    for s in &sf.scenes {
        let list: Vec<u64> = if raw_matches {
            keep_raw.as_ref().unwrap().scenes[s.i].keep.clone()
        } else {
            recompute_keep(&motion.intervals, s.f0, s.f1)
        };
        lists.push((s.i, list));
    }

    // extract every needed frame losslessly in one ffmpeg select pass
    let mut union: Vec<u64> = lists.iter().flat_map(|(_, l)| l.iter().copied()).collect();
    union.sort_unstable();
    union.dedup();
    let tmp = work.root.join(".keep_extract");
    let _ = std::fs::remove_dir_all(&tmp);
    keep::extract(&probe.path, &union, &tmp, "auto")?;
    // tmp files are 0000.png.. in ascending frame order of `union`

    ensure_dir(&work.scenes_root())?;
    let mut dirs = 0;
    for (idx, s) in sf.scenes.iter().enumerate() {
        let dir = work.scene_dir(s.i, s.t0);
        // keep/ is REH-owned: wipe before refill so re-exports across cut changes
        // never leave frames from the previous boundary set behind
        let _ = std::fs::remove_dir_all(dir.join("keep"));
        ensure_dir(&dir.join("keep"))?;
        // move this scene's frames in
        for (pos, f) in union.iter().enumerate() {
            let belongs = lists.iter().any(|(i, l)| *i == s.i && l.contains(f));
            if !belongs {
                continue;
            }
            let src = tmp.join(format!("{pos:04}.png"));
            let dst = dir.join("keep").join(format!("{:06}.png", f + 1));
            if exists(&src) {
                let _ = std::fs::remove_file(&dst);
                std::fs::rename(&src, &dst).map_err(|e| format!("move keep frame {f}: {e}"))?;
            }
        }
        let (_, list) = &lists[idx];

        let in_cut = sf.cuts.iter().find(|c| c.f == s.f0 && c.source != *"audio");
        let out_cut = sf.cuts.iter().find(|c| c.f == s.f1 + 1 && c.source != *"audio");
        let motions: Vec<MotionRef> = motion
            .intervals
            .iter()
            .filter(|iv| iv.kind == "motion" && iv.f0 >= s.f0 && iv.f0 <= s.f1)
            .map(|iv| MotionRef {
                kind: iv.kind.clone(),
                f0: iv.f0,
                f1: iv.f1,
                bbox: iv.bbox,
                peak_f: iv.peak_f,
            })
            .collect();
        let t_in = (s.t0, s.t1);
        let meta = Meta {
            i: s.i,
            dir: s.dir.clone(),
            f0: s.f0,
            f1: s.f1,
            t0: s.t0,
            t1: s.t1,
            r#in: in_cut.map(|c| Boundary { f: c.f, kind: c.kind.clone() }),
            out: out_cut.map(|c| Boundary { f: c.f, kind: c.kind.clone() }),
            motions,
            audio_hints: AudioHints {
                silence_in: wf
                    .silence
                    .iter()
                    .filter(|z| z.t0 >= t_in.0 - 0.05 && z.t1 <= t_in.1 + 0.05)
                    .map(|z| [z.t0, z.t1])
                    .collect(),
                beats_in: wf
                    .beats
                    .iter()
                    .filter(|b| b.t >= t_in.0 && b.t <= t_in.1)
                    .map(|b| b.t)
                    .collect(),
            },
            keep_f: list.clone(),
        };
        write_json(&dir.join("meta.json"), &meta)?;
        write_text(&dir.join("notes.md"), &notes_md(&meta, &sf, &fps))?;
        dirs += 1;
    }
    let _ = std::fs::remove_dir_all(&tmp);
    // tidy empty scene dirs when cut count shrank (stale higher-numbered dirs)
    if let Ok(rd) = std::fs::read_dir(work.scenes_root()) {
        for e in rd.flatten() {
            let name = e.file_name().to_string_lossy().to_string();
            let idx_ok = sf
                .scenes
                .iter()
                .any(|s| s.dir == name && work.scene_dir(s.i, s.t0) == e.path());
            if !idx_ok && e.path().is_dir() && name.len() == 9 && name[..2].chars().all(|c| c.is_ascii_digit()) {
                let _ = std::fs::remove_dir_all(e.path());
            }
        }
    }
    Ok(dirs)
}

fn recompute_keep(intervals: &[Interval], f0: u64, f1: u64) -> Vec<u64> {
    let mut cands = vec![f0, f1];
    for iv in intervals
        .iter()
        .filter(|i| i.kind == "motion" && i.f0 >= f0 && i.f1 <= f1)
    {
        if iv.f0 > f0 {
            cands.push(iv.f0 - 1);
        }
        if iv.f1 + 1 <= f1 {
            cands.push(iv.f1 + 1);
        }
    }
    cands.sort_unstable();
    cands.dedup();
    cands.retain(|f| *f >= f0 && *f <= f1);
    cands
}

fn notes_md(m: &Meta, sf: &ScenesFile, _fps: &Fps) -> String {
    let mut s = String::new();
    s.push_str(&format!(
        "# Scene {:02} · {:.3}s → {:.3}s (frames {}–{} of {} @ {}/{} fps)\n\n",
        m.i, m.t0, m.t1, m.f0, m.f1, sf.nb_frames, sf.fps[0], sf.fps[1]
    ));
    s.push_str(&match (&m.r#in, &m.out) {
        (None, None) => "Boundaries: whole video (no cuts).\n".into(),
        _ => format!(
            "Boundary in: {} · Boundary out: {}\n",
            m.r#in
                .as_ref()
                .map(|b| format!("{} cut @frame {}", b.kind, b.f))
                .unwrap_or_else(|| "video start".into()),
            m.out
                .as_ref()
                .map(|b| format!("{} cut @frame {} (first frame of next scene)", b.kind, b.f))
                .unwrap_or_else(|| "end of video".into()),
        ),
    });
    if m.motions.is_empty() {
        s.push_str("\nMotion: none detected (fully static scene).\n");
    } else {
        s.push_str("\nMotion intervals (native-px bbox; pre frame = f0-1, post/settle frame = f1+1):\n");
        for iv in &m.motions {
            s.push_str(&format!(
                "- {} f{}→f{} bbox [{},{},{},{}] peak f{}\n",
                iv.kind, iv.f0, iv.f1, iv.bbox[0], iv.bbox[1], iv.bbox[2], iv.bbox[3], iv.peak_f
            ));
        }
    }
    if !m.audio_hints.beats_in.is_empty() || !m.audio_hints.silence_in.is_empty() {
        s.push_str("\nAudio hints (non-authoritative):\n");
        if !m.audio_hints.beats_in.is_empty() {
            s.push_str(&format!("- beats: {:?}\n", m.audio_hints.beats_in));
        }
        for z in &m.audio_hints.silence_in {
            s.push_str(&format!("- silence {:.2}s–{:.2}s\n", z[0], z[1]));
        }
    }
    s.push_str(&format!(
        "\nkeep/ frames (name = source frame + 1, lossless): {}\n",
        m.keep_f
            .iter()
            .map(|f| format!("{:06}.png(f{f})", f + 1))
            .collect::<Vec<_>>()
            .join(", ")
    ));
    s.push_str(&format!(
        "\nAuthor questions (answer in description.md): what elements exist and where \
         (absolute px on {W}x{H}); what is static vs animated; motion from→to with \
         timing/easing between keep frames; text content verbatim; transition in/out.\n",
        W = sf.width,
        H = sf.height
    ));
    s
}

// ---------- commands ----------

pub fn dispatch(c: ScenesCmd) -> R<()> {
    match c {
        ScenesCmd::Review { work } => review(&Work { root: work }),
        ScenesCmd::Apply(a) => apply(a),
        ScenesCmd::Export { work } => {
            let w = Work { root: work };
            build_scenes(&w)?;
            let n = export(&w)?;
            println!("exported {n} scene folders");
            Ok(())
        }
    }
}

fn load_work(work: &PathBuf) -> R<Work> {
    let w = Work { root: work.clone() };
    if !exists(&w.probe()) {
        return Err(bail(format!(
            "{} missing probe.json — run `reh run <video>` first",
            w.root.display()
        )));
    }
    Ok(w)
}

fn review(w: &Work) -> R<()> {
    let sf: ScenesFile = if exists(&w.scenes_json()) {
        read_json(&w.scenes_json())?
    } else {
        build_scenes(w)?
    };
    let dur = probe::load(w).map(|p| p.duration_s).unwrap_or_else(|_| {
        sf.cuts.iter().map(|c| c.t).fold(0.0f64, |a, b| a.max(b))
    });
    let user_cut = exists(&w.cuts_json());
    println!(
        "{} · {}x{} {}/{} fps · {} frames · {:.2}s",
        w.root.display(),
        sf.width,
        sf.height,
        sf.fps[0],
        sf.fps[1],
        sf.nb_frames,
        dur
    );
    let active: Vec<&CutRow> = sf
        .cuts
        .iter()
        .filter(|c| c.source != "audio")
        .collect();
    println!(
        "\nboundaries ({}; source {}):\n {:>6} {:>10}  {:<6} {:<8} {}",
        active.len(),
        if user_cut && active.iter().any(|c| c.source == "user") {
            "USER cuts.json"
        } else {
            "inferred"
        },
        "frame",
        "t(s)",
        "src",
        "kind",
        "conf",
    );
    for c in &active {
        println!(" {:>6} {:>10.3}  {:<6} {:<8} {:.2}", c.f, c.t, c.source, c.kind, c.conf);
    }
    let beats: Vec<&CutRow> = sf.cuts.iter().filter(|c| c.source == "audio").collect();
    if !beats.is_empty() {
        println!("\naudio beat hints (never boundaries on their own):");
        for b in beats.iter().take(24) {
            println!("  {:>10.3}s conf {:.2}", b.t, b.conf);
        }
        if beats.len() > 24 {
            println!("  … {} more", beats.len() - 24);
        }
    }
    println!("\nscenes:");
    for s in &sf.scenes {
        println!("  {} → {}  {:<12}", s.dir, format_args!("{:.3}", s.t1), format!("{}–{}", s.f0, s.f1));
    }
    println!(
        "\naccept:  reh scenes apply {} --lgtm\ncorrect: reh scenes apply {} --cuts \"00:00:12, ...\"\nor UI:   reh preview {}",
        w.root.display(),
        w.root.display(),
        w.root.display()
    );
    Ok(())
}

fn apply(a: ApplyArgs) -> R<()> {
    let w = load_work(&a.work)?;
    let probe: Probe = probe::load(&w)?;
    let fps = probe.fps();
    let explicit = a.cuts.as_ref().map(|s| -> R<Vec<UserCut>> {
        let mut out = Vec::new();
        for part in s.split(',') {
            if part.trim().is_empty() {
                continue;
            }
            out.push(UserCut {
                f: None,
                t: parse_time(part)?,
            });
        }
        if out.is_empty() {
            return Err(bail("--cuts produced nothing"));
        }
        Ok(out)
    }).transpose()?.unwrap_or_default();
    let from_file = match &a.file {
        Some(p) => {
            let cf: CutsFile = read_json(p)?;
            cf.cuts
        }
        None => Vec::new(),
    };
    let mut cuts = explicit;
    cuts.extend(from_file);
    if cuts.is_empty() && !a.lgtm {
        return Err(bail("nothing to apply: pass --lgtm, --cuts \"...\" or --file cuts.json"));
    }
    let cf = CutsFile {
        cuts,
        confirmed: true,
    };
    write_json(&w.cuts_json(), &cf)?;
    let sf = build_scenes(&w)?;
    let n = export(&w)?;
    let _ = fps;
    println!(
        "applied: {} user cut point(s){} · {} scenes · exported {n} folders",
        cf.cuts.len(),
        if a.lgtm && cf.cuts.is_empty() {
            " (LGTM: inferred cuts stand)"
        } else {
            ""
        },
        sf.scenes.len()
    );
    Ok(())
}
