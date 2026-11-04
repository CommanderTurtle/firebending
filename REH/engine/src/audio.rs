use crate::ff::{ffmpeg, run, spawn_stdout_pipe};
use crate::probe::Probe;
use crate::util::{exists, write_json, Work, R};
use rustfft::num_complex::Complex32;
use rustfft::FftPlanner;
use serde::{Deserialize, Serialize};
use std::io::Read;
use std::process::Command;

const PEAK_RATE: u32 = 50;
const SILENCE_NOISE_DB: &str = "-35dB";
const SILENCE_MIN_DUR: &str = "0.25";

#[derive(Serialize, Deserialize, Clone)]
pub struct Silence {
    pub t0: f64,
    pub t1: f64,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct Beat {
    pub t: f64,
    pub strength: f64,
}

#[derive(Serialize, Deserialize)]
pub struct Waveform {
    pub rate: u32,
    pub peaks: Vec<f32>,
    pub silence: Vec<Silence>,
    pub beats: Vec<Beat>,
}

pub fn ext_for(acodec: &str) -> &'static str {
    match acodec {
        "aac" => "m4a",
        "mp3" => "mp3",
        "opus" => "opus",
        "vorbis" => "ogg",
        "flac" => "flac",
        "alac" => "m4a",
        c if c.starts_with("pcm") => "wav",
        _ => "mka",
    }
}

/// Lossless codec-copy split so the video pass never carries audio, and the
/// original stream survives byte-faithful for pass-2 music awareness.
pub fn remux(w: &Work, probe: &Probe, force: bool) -> R<Option<String>> {
    let Some(acodec) = probe.acodec.clone() else {
        return Ok(None);
    };
    let ext = ext_for(&acodec).to_string();
    let out = w.audio_named(&ext);
    if exists(&out) && !force {
        return Ok(Some(ext));
    }
    let mut args: Vec<std::ffi::OsString> = Vec::new();
    args.extend(["-i"].iter().map(|s| s.to_string().into()));
    args.push(probe.path.clone().into());
    args.extend(["-vn", "-c", "copy", "-y"].iter().map(|s| s.to_string().into()));
    if ext == "m4a" {
        args.extend(["-f", "ipod"].iter().map(|s| s.to_string().into()));
    }
    args.push(out.into());
    run(ffmpeg("none", &args_iter(&args)), &format!("audio remux ({ext})"))?;
    Ok(Some(ext))
}

fn args_iter<'a>(v: &'a [std::ffi::OsString]) -> Vec<&'a str> {
    // paths here are utf8 by construction (probe.path is a String)
    v.iter().map(|s| s.to_str().unwrap_or_default()).collect()
}

/// s16 mono PCM decode at fixed rate (analysis-only; the stored audio stays lossless).
fn pcm(video: &str, rate: u32) -> R<Vec<i16>> {
    let mut cmd = ffmpeg(
        "none",
        &[
            "-i",
            video,
            "-vn",
            "-ac",
            "1",
            "-ar",
            &rate.to_string(),
            "-f",
            "s16le",
            "-",
        ],
    );
    cmd.args(["-loglevel", "quiet"]);
    let mut spawned = spawn_stdout_pipe(cmd, "pcm decode")?;
    let mut buf = Vec::new();
    spawned
        .child
        .stdout
        .take()
        .expect("pipe")
        .read_to_end(&mut buf)?;
    spawned.finish("pcm decode")?;
    Ok(buf
        .chunks_exact(2)
        .map(|b| i16::from_le_bytes([b[0], b[1]]))
        .collect())
}

pub fn silence_marks(video: &str) -> R<Vec<Silence>> {
    // silencedetect logs at info level; parse its stderr
    let mut cmd = Command::new("ffmpeg");
    cmd.args(["-hide_banner", "-nostdin", "-i", video, "-vn"])
        .arg("-af")
        .arg(format!(
            "silencedetect=noise={SILENCE_NOISE_DB}:d={SILENCE_MIN_DUR}"
        ))
        .args(["-f", "null", "-"]);
    let out = run(cmd, "silencedetect")?;
    let text = String::from_utf8_lossy(&out.stderr).into_owned();
    let mut marks: Vec<Silence> = Vec::new();
    let mut open: Option<f64> = None;
    for line in text.lines() {
        if let Some(v) = line.strip_prefix("[silencedetect @ 0x7fffffffffff] silence_start: ")
            .or_else(|| line.split("silence_start: ").nth(1))
        {
            if let Ok(v) = v.trim().parse::<f64>() {
                open = Some(v);
            }
        } else if let Some(rest) = line.split("silence_end: ").nth(1) {
            let v: &str = rest.split('|').next().unwrap_or("").trim();
            if let (Some(t0), Ok(t1)) = (open, v.parse::<f64>()) {
                marks.push(Silence {
                    t0: r3(t0),
                    t1: r3(t1),
                });
                open = None;
            }
        }
    }
    Ok(marks)
}

/// Spectral-flux onset detector: 1024-pt Hann, 50 Hz hops, 24 log bands.
/// Catches beat-synced cuts without any model, per the "hint" contract.
pub fn beats(video: &str) -> R<Vec<Beat>> {
    const SR: u32 = 22050;
    const WIN: usize = 1024;
    const HOP: usize = 441; // 50 Hz
    let pcm = pcm(video, SR)?;
    if pcm.len() < WIN * 2 {
        return Ok(Vec::new());
    }
    let mut planner = FftPlanner::<f32>::new();
    let fft = planner.plan_fft_forward(WIN);
    let mut hann = vec![0f32; WIN];
    for i in 0..WIN {
        hann[i] = 0.5 * (1.0 - (2.0 * std::f64::consts::PI * i as f64 / (WIN - 1) as f64).cos() as f32);
    }
    // log band edges 60 Hz .. 10 kHz
    let nbands = 24usize;
    let bin_hz = SR as f64 / WIN as f64;
    let mut band_bins: Vec<(usize, usize)> = Vec::new();
    for b in 0..nbands {
        let f0 = 60f64 * (10000.0f64 / 60.0).powf(b as f64 / nbands as f64);
        let f1 = 60f64 * (10000.0f64 / 60.0).powf((b + 1) as f64 / nbands as f64);
        let i0 = ((f0 / bin_hz) as usize).max(1);
        let i1 = ((f1 / bin_hz) as usize).min(WIN / 2).max(i0 + 1);
        band_bins.push((i0, i1));
    }
    let nhops = (pcm.len() - WIN) / HOP + 1;
    let mut flux = vec![0f32; nhops];
    let mut prev_mag: Vec<f32> = vec![0.0; nbands];
    let mut frame = vec![Complex32::new(0f32, 0f32); WIN];
    for h in 0..nhops {
        let off = h * HOP;
        for i in 0..WIN {
            frame[i] = Complex32::new(pcm[off + i] as f32 * hann[i], 0.0);
        }
        fft.process(&mut frame);
        let mut acc = 0f32;
        for (b, (i0, i1)) in band_bins.iter().enumerate() {
            let mut e = 0f32;
            for k in *i0..*i1 {
                e += frame[k].norm_sqr();
            }
            let mag = e.ln_1p();
            acc += (mag - prev_mag[b]).max(0.0);
            prev_mag[b] = mag;
        }
        flux[h] = acc;
    }
    let max = flux.iter().cloned().fold(0f32, f32::max);
    if max <= 0.0 {
        return Ok(Vec::new());
    }
    let mean = flux.iter().sum::<f32>() / nhops as f32;
    let var = flux.iter().map(|v| (v - mean) * (v - mean)).sum::<f32>() / nhops as f32;
    let th = mean + 0.75 * var.sqrt();
    let refractory = 12usize; // 0.24 s
    let mut out: Vec<Beat> = Vec::new();
    let mut last = isize::MAX / 2;
    for h in 2..nhops.saturating_sub(2) {
        let v = flux[h];
        if v < th {
            continue;
        }
        let is_peak = v >= flux[h - 2] && v >= flux[h - 1] && v > flux[h + 1] && v >= flux[h + 2];
        if !is_peak {
            continue;
        }
        if (h as isize - last) < refractory as isize {
            if let Some(b) = out.last_mut() {
                let s = (v / max) as f64;
                if s > b.strength {
                    b.t = r3(h as f64 / PEAK_RATE as f64);
                    b.strength = s;
                    last = h as isize;
                }
            }
            continue;
        }
        out.push(Beat {
            t: r3(h as f64 / PEAK_RATE as f64),
            strength: r3((v / max) as f64),
        });
        last = h as isize;
    }
    Ok(out)
}

pub fn build(w: &Work, probe: &Probe, no_beats: bool, force: bool) -> R<Waveform> {
    if exists(&w.waveform()) && !force {
        return Ok(read_wf(w)?);
    }
    let peaks = if probe.acodec.is_some() {
        let samples = pcm(&probe.path, 48000)?;
        let bucket = (48000 / PEAK_RATE) as usize; // 960
        let mut peaks = Vec::with_capacity(samples.len() / bucket + 1);
        for chunk in samples.chunks(bucket) {
            let m = chunk.iter().map(|s| (*s as i32).unsigned_abs()).max().unwrap_or(0);
            peaks.push(((m as f64 / 32768.0) * 1e4).round() as f32 / 1e4);
        }
        peaks
    } else {
        Vec::new()
    };
    let silence = if probe.acodec.is_some() {
        silence_marks(&probe.path).unwrap_or_default()
    } else {
        Vec::new()
    };
    let beats = if probe.acodec.is_some() && !no_beats {
        beats(&probe.path).unwrap_or_default()
    } else {
        Vec::new()
    };
    let wf = Waveform {
        rate: PEAK_RATE,
        peaks,
        silence,
        beats,
    };
    write_json(&w.waveform(), &wf)?;
    Ok(wf)
}

fn read_wf(w: &Work) -> R<Waveform> {
    crate::util::read_json(&w.waveform())
}

fn r3(x: f64) -> f64 {
    (x * 1e3).round() / 1e3
}
