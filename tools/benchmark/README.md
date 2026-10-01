# Benchmark AL Runner on Microsoft's full test surface

Thank you for lending a machine. This script runs about 40,000 of Microsoft's own Business Central tests through AL Runner and packs the timings into one zip file for you to send back.

## What you need

- **Windows 10 or 11, 64-bit, on an x64 processor** (Linux x64 works too). Not ARM, not macOS.
- **At least 16 GB of RAM.** More memory lets the script run more buckets at the same time; 64 GB or more is ideal.
- **About 30 GB of free disk space** on the drive you run it from. An SSD is strongly preferred.
- **An internet connection** that can reach Microsoft's download servers, nuget.org and github.com.
- **Nothing else.** You don't need .NET, Business Central, Docker, SQL Server or administrator rights. The script downloads a private copy of everything it needs into one folder.

## Run it

Open **PowerShell** (the normal Windows PowerShell is fine) and paste the command you were sent. It looks like this, with the two `<...>` parts filled in:

```powershell
$d = "$HOME\al-runner-benchmark"; New-Item -ItemType Directory -Force $d | Out-Null
Invoke-WebRequest "https://raw.githubusercontent.com/StefanMaron/BusinessCentral.AL.Runner/<COMMIT>/tools/benchmark/ms-surface-benchmark.ps1" -OutFile "$d\ms-surface-benchmark.ps1" -UseBasicParsing
if ((Get-FileHash "$d\ms-surface-benchmark.ps1").Hash -ne '<SHA256>') { throw 'The download does not match the expected checksum. Do not run it.' }
powershell -NoProfile -ExecutionPolicy Bypass -File "$d\ms-surface-benchmark.ps1"
```

The command downloads the script from a fixed version of the repository, checks that it is exactly the file that was reviewed, and runs it. `-ExecutionPolicy Bypass` applies to that one PowerShell process only; it does not change your system settings.

If your user folder path is long (more than 40 characters) and Windows long paths are off, the script stops and asks you to use a shorter folder. Add `-WorkDir C:\alrb` to the last line in that case.

## How long it takes

The first 10 to 20 minutes are downloads (about 1.5 GB) and one small "smoke" bucket that proves the setup works. Then the full run starts. On GitHub's 4-core cloud machines the full surface takes about 4 hours one bucket at a time. A desktop with many cores and plenty of memory runs several buckets at once and should finish well within that. The longest single bucket takes about an hour on its own, so no machine finishes faster than that.

The script prints a line each time a bucket starts or finishes, and a status line every 5 minutes.

## Stopping and resuming

Press **Ctrl+C** to stop. Buckets that were running are stopped too. Run the same command again later to continue: finished downloads and finished buckets are kept, and only the rest is run. The summary then says the run was resumed, because the total time no longer covers one sitting.

## What it does to your machine

Everything goes into one folder, `al-runner-benchmark` in your user folder by default. To remove everything afterwards, delete that folder.

The script does **not** install anything system-wide, change PATH, change Defender or firewall settings, or need administrator rights. It uses a lot of CPU and memory while it runs, so the machine will be slow for other work.

**Microsoft Defender:** if real-time protection is on, the script says so. Defender scans every file the runner writes, which slows the run. If you are comfortable with it, you can exclude the folder yourself from an administrator PowerShell before starting, and remove the exclusion afterwards (the script prints both commands). The result records whether Defender was on, so either way the numbers are usable.

## When it stops early

If something is wrong, the script stops with a message starting with `STOPPED:` followed by what to do. It never reports a half-finished or broken run as a result. Common reasons:

- Not enough memory or disk space. Close other programs or pick another drive with `-WorkDir`.
- A download host is blocked. Try another network.
- **The smoke bucket is not sane.** The setup did not work on this machine. Please send the `results\smoke` folder from the working folder back instead; it is exactly what we need to fix it.

## What to send back

When it finishes, the script prints the path of a file named `al-runner-benchmark-<date>.zip` in the working folder. Send that file. It contains:

- `summary.md` and `summary.json`: per-bucket test counts, pass/fail, wall time and peak memory, the total, and the worker count and why it was chosen
- your machine's CPU model, core count, memory, Windows version, disk type, power plan and Defender state (no machine name)
- the exact versions and command lines used, and every log

The logs contain the working-folder path, which includes your Windows user name. Edit it out if you prefer; nothing else in the zip identifies you.

## For maintainers

- The script mirrors the configuration of `.github/workflows/ms-bucket.yml`: `--test-data` with company `CRONUS International Ltd_`, both package caches, `AL_RUNNER_EMIT_TIMEOUT_SEC=3600`, a private `--cache`, the pinned reader, and a per-test timeout of at least 300 s. Runner, BC build, reader, .NET and PowerShell versions are pinned with checksums at the top of the script.
- It runs one runner process per bucket and keeps up to N of them going at once. That is the same process-level sharding `--jobs` does, with the same per-worker GC settings. It does not call `--jobs` itself, because with `--jobs` the runner writes neither the `--output-junit` file nor a complete `--out` file (#5129), and one runner process holding several buckets adds up their memory. A parent folder holding all buckets is not split by `--jobs` at all: the runner reads it as one bundle and runs it in one process.
- N comes from memory: available memory minus a reserve (the larger of 4 GB and 10% of RAM), divided by 2.5 times the smoke bucket's measured peak (at least 4 GB per worker). It is then capped by physical cores, by the number of buckets, and by how many workers can help at all when the longest bucket cannot be split. The script prints all four caps and which one decided. A new bucket only starts when the memory for it is actually free. `-Jobs N` overrides the count.
- To produce the volunteer command, take a commit of `main` that contains the script, and fill `<COMMIT>` with that commit SHA and `<SHA256>` with `(Get-FileHash ms-surface-benchmark.ps1).Hash` of the file at that commit.
- `.github/workflows/ms-surface-benchmark-windows.yml` runs the script on `windows-latest` the way a volunteer starts it: the failure stops, a reduced surface with two workers to a result zip, a resume, and Tests-ERM alone for its memory peak.
