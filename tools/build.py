"""Test, publish and package COD Downgrader.

    python tools/build.py             unit tests, then the release set for the version in Directory.Build.props
    python tools/build.py --no-test   skip the tests
    python tools/build.py --linux     the Linux build as well, which is not released yet, into dist/v<version>-linux/

dist/ ends up holding:

    win-x64/CODDowngrader.exe            the published window program on its own
    win-x64/CODDowngrader.com            its console face, the NativeAOT stub
    v<version>/
      COD-DG-v<version>-win-x64.zip      release asset: both, README, LICENSE and notices
      SHA256SUMS                         release asset: the zip's hash
      release-notes.md                   \
      commit-message.md                   |  release documents, kept across builds. A missing one is
      commit-message-short.md            /   created as a placeholder marked <!-- unwritten -->.

    v<version>-linux/                    with --linux only, and never uploaded: the .tar.gz of the Linux program with its
                                         Steam Deck installer, README, LICENSE and notices, and its own SHA256SUMS

Asset names have no spaces: GitHub rewrites spaces in uploaded names, and SHA256SUMS has to name the
file people actually download.
"""
import argparse
import hashlib
import io
import os
import re
import shutil
import subprocess
import tarfile
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "src" / "CODDowngrader" / "CODDowngrader.csproj"
STUB = ROOT / "src" / "CODDowngrader.Stub" / "CODDowngrader.Stub.csproj"
PROPS = ROOT / "Directory.Build.props"
TESTS = ROOT / "tests" / "CODDowngrader.Tests" / "CODDowngrader.Tests.csproj"
DIST = ROOT / "dist"
TARGETS = {"win-x64": ("CODDowngrader.exe", "CODDowngrader.com")}
# Held back from the release set until it has run on Linux and a Steam Deck.
LINUX_TARGETS = {"linux-x64": ("CODDowngrader",)}
LINUX_PACKAGING = ROOT / "packaging" / "linux"
ICON = ROOT / "src" / "CODDowngrader" / "Assets" / "icon.png"
# NativeAOT links with Visual Studio's C++ tools, and its targets call vswhere.exe by name.
VSWHERE_DIR = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Microsoft Visual Studio" / "Installer"
ZIP_FOLDER = "COD Downgrader"
ZIP_DOCUMENTS = ("README.md", "LICENSE", "THIRD-PARTY-NOTICES.md")
UNWRITTEN = "<!-- unwritten -->"
DOCUMENTS = {
    "release-notes.md": "The body of the GitHub release for v{version}: player-facing, what it is, what is new, what to download.",
    "commit-message.md": "The release commit for v{version}: subject line, blank line, body.",
    "commit-message-short.md": "The same commit squeezed: subject plus a few bullets.",
}


def project_property(name: str) -> str:
    for source in (PROJECT, PROPS):
        match = re.search(rf"<{name}>([^<]+)</{name}>", source.read_text(encoding="utf-8"))
        if match:
            return match.group(1)
    raise SystemExit(f"No <{name}> in {PROJECT} or {PROPS}")


def run(*command, env=None) -> None:
    print(">", " ".join(str(part) for part in command), flush=True)
    subprocess.run([str(part) for part in command], check=True, env=env)


def dotnet_notices(rid: str) -> Path:
    """The THIRD-PARTY-NOTICES.TXT of the exact .NET runtime pack published into the exe."""
    deps = PROJECT.parent / "bin" / "Release" / project_property("TargetFramework") / rid / "CODDowngrader.deps.json"
    match = re.search(r'runtimepack\.Microsoft\.NETCore\.App\.Runtime\.[^/"]+/([0-9][^"]*)', deps.read_text(encoding="utf-8"))
    if not match:
        raise SystemExit(f"{deps} names no .NET runtime pack")
    packages = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget" / "packages")
    notices = packages / f"microsoft.netcore.app.runtime.{rid}" / match.group(1) / "THIRD-PARTY-NOTICES.TXT"
    if not notices.exists():
        raise SystemExit(f"The .NET runtime's notices are not at {notices}")
    return notices


def publish_stub(rid: str, out: Path) -> None:
    """CODDowngrader.com: the console stub, published with NativeAOT and renamed from the .exe it is built as."""
    stub_out = DIST / f"{rid}-stub"
    if stub_out.exists():
        shutil.rmtree(stub_out)
    env = dict(os.environ)
    env["PATH"] = f"{VSWHERE_DIR}{os.pathsep}{env.get('PATH', '')}"
    run("dotnet", "publish", STUB, "-c", "Release", "-r", rid, "-o", stub_out, "-nologo", "-v", "minimal", env=env)
    shutil.copyfile(stub_out / "CODDowngrader.exe", out / "CODDowngrader.com")
    shutil.rmtree(stub_out)


def package_notices(rid: str) -> dict:
    """
    The notices of the native libraries the window carries, as SkiaSharp's package ships them. HarfBuzzSharp comes from the
    same repository and ships the same file; if that ever changes, the build stops so the second file gets shipped too.
    """
    deps = (PROJECT.parent / "bin" / "Release" / project_property("TargetFramework") / rid / "CODDowngrader.deps.json").read_text(encoding="utf-8")
    packages = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget" / "packages")
    found = {}
    for package in ("SkiaSharp", "HarfBuzzSharp"):
        match = re.search(rf'"{package}/([0-9][^"]*)"', deps)
        if not match:
            raise SystemExit(f"{rid}'s deps.json names no {package}")
        notices = packages / package.lower() / match.group(1) / "THIRD-PARTY-NOTICES.txt"
        if not notices.exists():
            raise SystemExit(f"{package}'s notices are not at {notices}")
        found[package] = notices
    if found["SkiaSharp"].read_bytes() != found["HarfBuzzSharp"].read_bytes():
        raise SystemExit(f"HarfBuzzSharp's notices differ from SkiaSharp's now: ship {found['HarfBuzzSharp']} as well")
    return {"THIRD-PARTY-NOTICES-SKIASHARP.TXT": found["SkiaSharp"]}


def package(rid: str, files: tuple, version: str, release: Path) -> Path:
    out = DIST / rid
    if out.exists():
        shutil.rmtree(out)
    run("dotnet", "publish", PROJECT, "-c", "Release", "-r", rid, "-o", out, "-nologo", "-v", "minimal")
    if "CODDowngrader.com" in files:
        publish_stub(rid, out)

    if rid.startswith("linux"):
        return package_tar(rid, out, files, version, release)

    zip_path = release / f"COD-DG-v{version}-{rid}.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for exe in files:
            binary = zipfile.ZipInfo.from_file(out / exe, f"{ZIP_FOLDER}/{exe}")
            binary.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(binary, (out / exe).read_bytes())
        for name in ZIP_DOCUMENTS:
            archive.write(ROOT / name, f"{ZIP_FOLDER}/{name}")
        archive.write(dotnet_notices(rid), f"{ZIP_FOLDER}/THIRD-PARTY-NOTICES-DOTNET.TXT")
        for name, path in package_notices(rid).items():
            archive.write(path, f"{ZIP_FOLDER}/{name}")
    return zip_path


def package_tar(rid: str, out: Path, files: tuple, version: str, release: Path) -> Path:
    """Linux: a .tar.gz, which keeps the executable bit, with the Steam Deck installer beside the program."""
    tar_path = release / f"COD-DG-v{version}-{rid}.tar.gz"
    now = int(time.time())

    def add(archive, name: str, data: bytes, executable: bool) -> None:
        info = tarfile.TarInfo(f"{ZIP_FOLDER}/{name}")
        info.size = len(data)
        info.mode = 0o755 if executable else 0o644
        info.mtime = now
        archive.addfile(info, io.BytesIO(data))

    with tarfile.open(tar_path, "w:gz") as archive:
        for exe in files:
            add(archive, exe, (out / exe).read_bytes(), executable=True)
        for script in ("install.sh", "Install COD Downgrader.desktop"):
            data = (LINUX_PACKAGING / script).read_bytes()
            if bytes([13, 10]) in data:
                raise SystemExit(f"{script} has CRLF line endings; a shell reads the CR as part of every line")
            add(archive, script, data, executable=True)
        add(archive, "icon.png", ICON.read_bytes(), executable=False)
        for name in ZIP_DOCUMENTS:
            add(archive, name, (ROOT / name).read_bytes(), executable=False)
        add(archive, "THIRD-PARTY-NOTICES-DOTNET.TXT", dotnet_notices(rid).read_bytes(), executable=False)
        for name, path in package_notices(rid).items():
            add(archive, name, path.read_bytes(), executable=False)
    return tar_path


def write_sums(folder: Path, archives: list) -> None:
    sums = [f"{hashlib.sha256(a.read_bytes()).hexdigest()}  {a.name}" for a in archives]
    (folder / "SHA256SUMS").write_text("\n".join(sums) + "\n", encoding="utf-8", newline="\n")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--no-test", action="store_true", help="skip the unit tests")
    parser.add_argument("--linux", action="store_true", help="package the Linux build too, for testing it")
    args = parser.parse_args()

    version = project_property("Version")
    if not args.no_test:
        run("dotnet", "test", TESTS, "-nologo", "-v", "minimal")

    release = DIST / f"v{version}"
    release.mkdir(parents=True, exist_ok=True)
    for old in list(release.glob("*.zip")) + list(release.glob("*.tar.gz")) + [release / "SHA256SUMS"]:
        old.unlink(missing_ok=True)

    zips = [package(rid, files, version, release) for rid, files in TARGETS.items()]
    write_sums(release, zips)

    # The Linux build goes to a folder of its own, so nothing unreleased sits beside the release set.
    linux = DIST / f"v{version}-linux"
    for rid in LINUX_TARGETS:
        if (DIST / rid).exists():
            shutil.rmtree(DIST / rid)
    if linux.exists():
        shutil.rmtree(linux)
    tars = []
    if args.linux:
        linux.mkdir(parents=True)
        tars = [package(rid, files, version, linux) for rid, files in LINUX_TARGETS.items()]
        write_sums(linux, tars)

    for name, purpose in DOCUMENTS.items():
        path = release / name
        if not path.exists():
            path.write_text(f"{UNWRITTEN}\n<!-- {purpose.format(version=version)} -->\n", encoding="utf-8", newline="\n")

    print()
    for z in zips:
        print(f"{z.stat().st_size / 1_048_576:6.1f} MB  {z.relative_to(ROOT)}")
    print(f"Upload to the v{version} release: {', '.join(z.name for z in zips)}, SHA256SUMS")
    for tar in tars:
        print(f"{tar.stat().st_size / 1_048_576:6.1f} MB  {tar.relative_to(ROOT)}  (Linux, not part of the release)")
    unwritten = [name for name in DOCUMENTS if UNWRITTEN in (release / name).read_text(encoding="utf-8")]
    if unwritten:
        print(f"Unwritten in {release.relative_to(ROOT)}: {', '.join(unwritten)}")


if __name__ == "__main__":
    main()
