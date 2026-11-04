use crate::util::{bail, R};
use std::io::Read;
use std::process::{Command, Output, Stdio};

pub fn ffmpeg<'a>(hwaccel: &str, extra_args: &[&'a str]) -> Command {
    let mut c = Command::new("ffmpeg");
    c.arg("-hide_banner").arg("-nostdin").arg("-loglevel").arg("error");
    match hwaccel {
        "none" | "" => {}
        other => {
            c.arg("-hwaccel").arg(other);
        }
    }
    c.args(extra_args);
    c
}

pub fn ffprobe<'a>(args: &[&'a str]) -> Command {
    let mut c = Command::new("ffprobe");
    c.arg("-v").arg("error");
    c.args(args);
    c
}

pub fn run(mut cmd: Command, ctx: &str) -> R<Output> {
    let out = cmd
        .output()
        .map_err(|e| format!("{ctx}: spawn failed: {e} (is ffmpeg/ffprobe on PATH?)"))?;
    if !out.status.success() {
        return Err(bail(format!(
            "{ctx}: exit {} {}",
            out.status.code().unwrap_or(-1),
            String::from_utf8_lossy(&out.stderr).trim()
        )));
    }
    Ok(out)
}

/// Runs a command whose stdout is a raw byte stream (e.g. rawvideo/s16le pipes).
/// stderr is drained on a helper thread so the child can never stall on its log
/// pipe; returns the child with piped stdout — caller must drain stdout first.
pub fn spawn_stdout_pipe(mut cmd: Command, ctx: &str) -> R<Spawned> {
    cmd.stdout(Stdio::piped()).stderr(Stdio::piped());
    let mut child = cmd
        .spawn()
        .map_err(|e| format!("{ctx}: spawn failed: {e} (is ffmpeg on PATH?)"))?;
    let err = child.stderr.take().expect("piped stderr");
    let owner = ctx.to_string();
    let handle = std::thread::spawn(move || {
        use std::io::Read;
        let mut sink = Vec::new();
        let mut err = err;
        let _ = err.read_to_end(&mut sink);
        if !sink.is_empty() {
            let text = String::from_utf8_lossy(&sink).into_owned();
            let trimmed = text.trim();
            if !trimmed.is_empty() {
                eprintln!("{owner}: child stderr:\n{trimmed}");
            }
        }
    });
    Ok(Spawned { child, stderr_drain: Some(handle) })
}

pub struct Spawned {
    pub child: std::process::Child,
    pub stderr_drain: Option<std::thread::JoinHandle<()>>,
}

impl Spawned {
    /// After stdout is fully consumed: wait for the child and the log drain.
    pub fn finish(mut self, ctx: &str) -> R<()> {
        let out = self
            .child
            .wait_with_output()
            .map_err(|e| format!("{ctx}: wait failed: {e}"))?;
        if let Some(h) = self.stderr_drain.take() {
            let _ = h.join();
        }
        if !out.status.success() {
            return Err(bail(format!(
                "{ctx}: exit {} {}",
                out.status.code().unwrap_or(-1),
                String::from_utf8_lossy(&out.stderr).trim()
            )));
        }
        Ok(())
    }
}

pub fn read_exact_stream<R2: Read>(r: &mut R2, buf: &mut [u8]) -> R<usize> {
    // fills buf fully; returns bytes read (== buf.len()) or 0 at clean EOF
    let mut off = 0;
    while off < buf.len() {
        match r.read(&mut buf[off..]) {
            Ok(0) => return Ok(off),
            Ok(n) => off += n,
            Err(e) if e.kind() == std::io::ErrorKind::Interrupted => {}
            Err(e) => return Err(bail(format!("pipe read: {e}"))),
        }
    }
    Ok(off)
}

pub fn exe_exists(name: &str) -> bool {
    Command::new(name)
        .arg("-version")
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .is_ok()
}
