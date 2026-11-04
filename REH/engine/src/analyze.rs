use crate::ff::{ffmpeg, read_exact_stream, spawn_stdout_pipe};
use crate::probe::Probe;
use crate::util::{bail, ensure_dir, exists, write_json, Fps, Work, R};
use serde::{Deserialize, Serialize};
use std::fs::File;
use std::io::{BufWriter, Write};

// Tuning defaults for composed motion-graphics ("small delta in an otherwise identical color").
const BLOCK: usize = 8;
const BLOCK_MEAN_TH: u32 = 2; // block avg |luma delta| out of 255 counts as active
const ACT_START: f32 = 0.004; // frame fraction of active blocks that starts a motion run
const ACT_STOP: f32 = 0.0015; // below this we consider the frame still
const SETTLE_FRAMES: u32 = 8; // still frames required to end a motion interval
const CUT_HIST_HARD: f32 = 0.30; // 0.5 * L1 histogram distance = hard cut
const CUT_HIST_MID: f32 = 0.08; // sustained mid-distance frames = dissolve
const CUT_HIST_LOW: f32 = 0.05; // dissolve run ends below this
const CUT_SUPPRESS: u64 = 12; // frames after a cut where no new cut is emitted
const SIG_W: usize = 160;
const SIG_H: usize = 90;
const SIG_MAE_DEDUP: f32 = 1.5;

#[derive(Serialize)]
struct FrameRec {
    n: u64,
    t: f64,
    act: f32,
    cut: Option<&'static str>,
    cut_conf: f32,
    state: &'static str,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct Interval {
    pub kind: String, // motion | dissolve | hard
    pub f0: u64,
    pub f1: u64,
    pub t0: f64,
    pub t1: f64,
    pub bbox: [u32; 4],
    pub peak_f: u64,
    pub scene: usize,
}

pub struct Output {
    pub nb_frames: u64,
    pub motion_intervals: usize,
}

/// Mirrors ffmpeg `scale=AW:-2` rounding for the downstream raw stream.
fn dims(probe: &Probe, long_edge: u32) -> (u32, u32) {
    let (w, h) = (probe.width.max(1) as f64, probe.height.max(1) as f64);
    let aw = if probe.width >= probe.height {
        long_edge
    } else {
        ((w * long_edge as f64 / h).floor() as u32) & !1
    };
    let aw = aw.max(16);
    let ah = (((h * aw as f64 / w).floor() as u32) & !1).max(16);
    (aw, ah)
}

fn hist_of(g: &[u8], w: usize) -> [u32; 64] {
    let mut h = [0u32; 64];
    for (ri, row) in g.chunks(w.max(1)).enumerate() {
        if ri % 2 != 0 {
            continue;
        }
        let mut c = 0;
        while c < row.len() {
            h[(row[c] as usize) >> 2] += 1;
            c += 2;
        }
    }
    h
}

fn hist_dist(a: &[u32; 64], b: &[u32; 64]) -> f32 {
    let sa: u64 = a.iter().sum::<u32>() as u64;
    let sb: u64 = b.iter().sum::<u32>() as u64;
    if sa == 0 || sb == 0 {
        return 0.0;
    }
    let mut acc = 0.0f64;
    for i in 0..64 {
        acc += (a[i] as f64 / sa as f64 - b[i] as f64 / sb as f64).abs();
    }
    (0.5 * acc) as f32
}

struct MotionRun {
    first_active: u64,
    last_active: u64,
    peak_f: u64,
    peak_act: f32,
    bx0: usize,
    bx1: usize,
    by0: usize,
    by1: usize,
}

impl MotionRun {
    fn interval(&self, fps: &Fps, aw: usize, ah: usize, nw: u32, nh: u32) -> Interval {
        let sx = nw as f64 / aw as f64;
        let sy = nh as f64 / ah as f64;
        let x0 = (self.bx0 as f64 * BLOCK as f64 * sx).floor() as u32;
        let y0 = (self.by0 as f64 * BLOCK as f64 * sy).floor() as u32;
        let x1 = (((self.bx1 + 1) as f64 * BLOCK as f64) * sx).ceil() as u32;
        let y1 = (((self.by1 + 1) as f64 * BLOCK as f64) * sy).ceil() as u32;
        Interval {
            kind: "motion".into(),
            f0: self.first_active,
            f1: self.last_active,
            t0: r6(fps.t_of(self.first_active)),
            t1: r6(fps.t_of(self.last_active)),
            bbox: [x0.min(nw), y0.min(nh), x1.min(nw), y1.min(nh)],
            peak_f: self.peak_f,
            scene: 0,
        }
    }
}

fn r6(x: f64) -> f64 {
    (x * 1e6).round() / 1e6
}

pub fn run(
    w: &Work,
    probe: &Probe,
    scale: u32,
    jpeg_q: u32,
    png_all: bool,
    hwaccel: &str,
    force: bool,
) -> R<Output> {
    if exists(&w.frames_json()) && !force {
        return Err(bail("frames.json exists — pass --force to redo the analysis stage"));
    }
    ensure_dir(&w.frames_dir())?;
    if png_all {
        ensure_dir(&w.pngall_dir())?;
    }
    let (aw, ah) = dims(probe, scale);
    let (aw, ah) = (aw as usize, ah as usize);
    let fbytes = aw * ah;
    let fps = probe.fps();

    // ONE hwaccel decode feeds three outputs, each with its OWN filtergraph.
    // (-vf binds to the following output only — never make the scale global,
    //  or the JPEG dump silently becomes analysis-res.)
    let scale_filter = format!("scale={aw}:-2,format=gray");
    let mut cmd = ffmpeg(hwaccel, &["-i", probe.path.as_str(), "-an", "-y"]);
    cmd.args([
        "-map",
        "0:v:0",
        "-c:v",
        "mjpeg",
        "-q:v",
        &jpeg_q.to_string(),
        "-start_number",
        "1",
    ]);
    cmd.arg(w.frames_dir().join("%06d.jpg"));
    if png_all {
        cmd.args(["-map", "0:v:0", "-c:v", "png", "-start_number", "1"]);
        cmd.arg(w.pngall_dir().join("%06d.png"));
    }
    cmd.args(["-map", "0:v:0", "-vf", &scale_filter]);
    cmd.args(["-f", "rawvideo", "-pix_fmt", "gray", "-"]);
    let mut spawned = spawn_stdout_pipe(cmd, "decode+dump")?;
    let pipe = spawned.child.stdout.take().expect("piped stdout");
    let mut pipe = std::io::BufReader::with_capacity(4 << 20, pipe);

    let mut jsonl = BufWriter::new(File::create(w.frames_json())?);
    let blocks_x = aw / BLOCK;
    let blocks_y = ah / BLOCK;
    let nblocks = blocks_x * blocks_y;

    let mut cur = vec![0u8; fbytes];
    let mut prevbuf = vec![0u8; fbytes];
    let mut have_prev = false;
    let mut prev_hist: Option<[u32; 64]> = None;
    let mut sigs: Vec<Vec<u8>> = Vec::new();

    let mut suppress_until: u64 = 0;
    let mut cuts: Vec<(u64, &'static str, f32)> = Vec::new();
    let mut mid_run: Option<(u64, u64)> = None;
    let mut motion: Option<MotionRun> = None;
    let mut still_run = 0u32;
    let mut consec_act = 0u32;
    let mut intervals: Vec<Interval> = Vec::new();
    let mut n: u64 = 0;

    loop {
        let got = read_exact_stream(&mut pipe, &mut cur)?;
        if got == 0 {
            break;
        }
        if got < fbytes {
            return Err(bail(format!(
                "stream truncated mid-frame ({got}/{fbytes} bytes) — analysis dims desynced from stream"
            )));
        }
        let t = r6(fps.t_of(n));
        let hist = hist_of(&cur, aw);
        let histd = prev_hist.as_ref().map(|p| hist_dist(p, &hist)).unwrap_or(0.0);

        // blockwise absolute luma delta
        let (act, bbox) = if have_prev {
            let mut active = 0usize;
            let mut bx0 = usize::MAX;
            let mut bx1 = 0usize;
            let mut by0 = usize::MAX;
            let mut by1 = 0usize;
            for by in 0..blocks_y {
                for bx in 0..blocks_x {
                    let mut acc = 0u32;
                    for yy in 0..BLOCK {
                        let row = (by * BLOCK + yy) * aw + bx * BLOCK;
                        let a = &prevbuf[row..row + BLOCK];
                        let b = &cur[row..row + BLOCK];
                        for k in 0..BLOCK {
                            acc += a[k].abs_diff(b[k]) as u32;
                        }
                    }
                    if acc / (BLOCK as u32 * BLOCK as u32) >= BLOCK_MEAN_TH {
                        active += 1;
                        bx0 = bx0.min(bx);
                        bx1 = bx1.max(bx);
                        by0 = by0.min(by);
                        by1 = by1.max(by);
                    }
                }
            }
            let act = active as f32 / nblocks as f32;
            (act, if active > 0 { Some([bx0, bx1, by0, by1]) } else { None })
        } else {
            (0.0, None)
        };
        sigs.push(sig_of(&cur, aw, ah));

        // cut classification (hard = histogram spike; dissolve = sustained mid distance)
        let mut cut: Option<(&'static str, f32)> = None;
        if n >= 1 && n >= suppress_until {
            if histd >= CUT_HIST_HARD {
                cut = Some(("hard", histd.min(1.0)));
            } else if histd >= CUT_HIST_MID {
                match &mut mid_run {
                    Some((_, e)) => *e = n,
                    None => mid_run = Some((n, n)),
                }
            } else if histd < CUT_HIST_LOW {
                if let Some((s, e)) = mid_run.take() {
                    if e + 1 >= s + 3 {
                        cuts.push((s, "dissolve", 0.6));
                        suppress_until = e + 1 + CUT_SUPPRESS;
                    }
                }
            }
        }
        if let Some(c) = cut {
            cuts.push((n, c.0, c.1));
            suppress_until = n + CUT_SUPPRESS;
            mid_run = None;
        }
        let on_transition = histd >= CUT_HIST_MID || cut.is_some();

        // motion state machine (frozen during transitions so dissolves do not fake motion)
        if act > ACT_START {
            consec_act += 1;
        } else {
            consec_act = 0;
        }
        if !on_transition {
            match &mut motion {
                None => {
                    if consec_act >= 2 {
                        let start = n + 1 - consec_act as u64;
                        let b = bbox.unwrap_or([0, 0, 0, 0]);
                        motion = Some(MotionRun {
                            first_active: start,
                            last_active: n,
                            peak_f: n,
                            peak_act: act,
                            bx0: b[0],
                            bx1: b[1],
                            by0: b[2],
                            by1: b[3],
                        });
                    }
                }
                Some(mr) => {
                    mr.last_active = n;
                    if act > mr.peak_act {
                        mr.peak_act = act;
                        mr.peak_f = n;
                    }
                    if let Some(b) = bbox {
                        mr.bx0 = mr.bx0.min(b[0]);
                        mr.bx1 = mr.bx1.max(b[1]);
                        mr.by0 = mr.by0.min(b[2]);
                        mr.by1 = mr.by1.max(b[3]);
                    }
                    still_run = if act <= ACT_STOP { still_run + 1 } else { 0 };
                    if still_run >= SETTLE_FRAMES {
                        let mr = motion.take().unwrap();
                        intervals.push(mr.interval(&fps, aw, ah, probe.width, probe.height));
                        still_run = 0;
                    }
                }
            }
        }
        let state = if on_transition {
            "transition"
        } else if motion.is_some() {
            "motion"
        } else {
            "still"
        };
        let rec = FrameRec {
            n,
            t,
            act: (act * 1e4).round() / 1e4,
            cut: cut.map(|c| c.0),
            cut_conf: cut.map(|c| (c.1 * 100.0).round() / 100.0).unwrap_or(0.0),
            state,
        };
        writeln!(jsonl, "{}", serde_json::to_string(&rec)?)?;

        std::mem::swap(&mut cur, &mut prevbuf);
        have_prev = true;
        prev_hist = Some(hist);
        n += 1;
        if n % 1000 == 0 {
            eprintln!("analyze: {n} frames");
        }
    }
    jsonl.flush()?;
    spawned.finish("decode+dump")?;

    if let Some(mr) = motion.take() {
        intervals.push(mr.interval(&fps, aw, ah, probe.width, probe.height));
    }
    if let Some((s, e)) = mid_run.take() {
        if e + 1 >= s + 3 {
            cuts.push((s, "dissolve", 0.6));
        }
    }
    cuts.sort_by_key(|c| c.0);
    cuts.dedup_by_key(|c| c.0);

    // ---- post pass: scene boundaries, interval tagging, keep-frame candidates ----
    let nb = n;
    let boundaries: Vec<u64> = cuts
        .iter()
        .map(|c| c.0)
        .filter(|f| *f > 0 && *f < nb)
        .collect();
    let nscenes = boundaries.len() + 1;
    let scene_of = |f: u64| -> usize { boundaries.partition_point(|b| *b <= f) };

    for iv in &mut intervals {
        iv.scene = scene_of(iv.f0);
    }
    for (f, kind, _) in &cuts {
        intervals.push(Interval {
            kind: kind.to_string(),
            f0: *f,
            f1: *f,
            t0: r6(fps.t_of(*f)),
            t1: r6(fps.t_of(*f)),
            bbox: [0, 0, probe.width, probe.height],
            peak_f: *f,
            scene: scene_of(*f),
        });
    }
    intervals.sort_by_key(|i| (i.f0, i.f1));

    let mut keep_by_scene: Vec<Vec<u64>> = Vec::new();
    for s in 0..nscenes {
        let f0 = if s == 0 { 0 } else { boundaries[s - 1] };
        let f1 = if s + 1 < nscenes { boundaries[s] - 1 } else { nb.saturating_sub(1) };
        let mut cands: Vec<u64> = vec![f0, f1];
        for iv in intervals.iter().filter(|i| i.scene == s && i.kind == "motion") {
            if iv.f0 > f0 {
                cands.push(iv.f0 - 1); // pre-movement
            }
            if iv.f1 + 1 <= f1 {
                cands.push(iv.f1 + 1); // post-movement settle
            }
        }
        cands.sort_unstable();
        cands.dedup();
        let mut kept: Vec<u64> = Vec::new();
        for c in cands {
            if c < f0 || c > f1 {
                continue;
            }
            if let Some(&last) = kept.last() {
                if sig_mae(&sigs[last as usize], &sigs[c as usize]) < SIG_MAE_DEDUP {
                    continue;
                }
            }
            kept.push(c);
        }
        keep_by_scene.push(kept);
    }

    write_json(&w.motion_json(), &serde_json::json!({ "intervals": intervals }))?;
    write_json(
        &w.keep_raw(),
        &serde_json::json!({
            "scenes": (0..nscenes)
                .map(|s| serde_json::json!({"i": s, "keep": keep_by_scene[s]}))
                .collect::<Vec<_>>()
        }),
    )?;

    Ok(Output {
        nb_frames: nb,
        motion_intervals: intervals.iter().filter(|i| i.kind == "motion").count(),
    })
}

fn sig_of(g: &[u8], aw: usize, ah: usize) -> Vec<u8> {
    let mut s = vec![0u8; SIG_W * SIG_H];
    let sxs = (aw / SIG_W).max(1);
    let sys = (ah / SIG_H).max(1);
    let mut si = 0;
    for sy in 0..SIG_H {
        let y = (sy * sys).min(ah.saturating_sub(1));
        for sx in 0..SIG_W {
            let x = (sx * sxs).min(aw.saturating_sub(1));
            s[si] = g[y * aw + x];
            si += 1;
        }
    }
    s
}

fn sig_mae(a: &[u8], b: &[u8]) -> f32 {
    let mut acc = 0u32;
    for i in (0..a.len()).step_by(3) {
        acc += a[i].abs_diff(b[i]) as u32;
    }
    acc as f32 / (a.len() / 3) as f32
}
