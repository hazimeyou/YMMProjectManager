"""Generate and verify local synthetic inputs for the R34 acceptance plan."""

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import stat
import struct
import subprocess
import tempfile
import zipfile
import zlib


MANIFEST = "fixture-manifest.json"
LEGACY_HASHES = {
    "v0.2.1": "39ccead7f012f46cbce19c47caa58118515efc57e08ffa3e7233d2a668b42443",
    "v0.3.0": "7160fc60a3cdfcffb1d73d100d87c581d7b62bb498aeb4087551cd7aa8dfb2d7",
    "v1.0.1": "e127f07d39e21d916aa40fea4fede43f6ea7c66460650080b6f917491bd3aa98",
}
YMMPX_LIB_HASH = "9d6e0a5f1a6fe096098ce77f2fd7a970a83a5c7f0476844e441f003aaa10fe23"
SCENE_ID = "00000000-0000-4000-8000-000000000001"
MISSING = "pf/missing/clip.png"
MISSING_YMMPX = "ymmpx/media/absent.png"


def inspect_path(path):
    info = path.lstat()
    if (stat.S_ISLNK(info.st_mode) or
            getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT):
        raise ValueError(f"Reparse point or link: {path}")
    return info


def inspect_ancestors(path):
    if str(path).startswith(("\\\\", "//")):
        raise ValueError("Use a local fixture directory.")
    if os.name == "nt":
        if len(path.drive) != 2 or not path.root or ":" in str(path)[2:]:
            raise ValueError("Use a local path without an alternate data stream.")
        drive_type = ctypes.windll.kernel32.GetDriveTypeW
        drive_type.argtypes = (ctypes.c_wchar_p,)
        drive_type.restype = ctypes.c_uint
        if drive_type(path.anchor) != 3:  # DRIVE_FIXED
            raise ValueError("Fixture inputs must be on a local fixed drive.")
    for ancestor in reversed(path.parents):
        if not stat.S_ISDIR(inspect_path(ancestor).st_mode):
            raise ValueError(f"Not a directory: {ancestor}")


def local_temp_root():
    if os.name == "nt":
        # gettempdir() probes inherited TEMP by writing before it returns a path.
        local = os.environ.get("LOCALAPPDATA")
        if local is None or not Path(local).is_absolute():
            raise ValueError("LOCALAPPDATA must specify a local absolute path.")
        root = Path(local) / "Temp"
    else:
        root = Path(tempfile.gettempdir()).absolute()
    inspect_ancestors(root)
    if not stat.S_ISDIR(inspect_path(root).st_mode):
        raise ValueError("Temporary path is not an ordinary directory.")
    return root.resolve(strict=True)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def checked_bytes(path, expected, maximum_size):
    path = path.absolute()
    inspect_ancestors(path)
    info = inspect_path(path)
    if not stat.S_ISREG(info.st_mode) or not 0 < info.st_size <= maximum_size:
        raise ValueError(f"Unexpected input size or file type: {path.name}")
    with path.open("rb") as source:
        data = source.read(maximum_size + 1)
    if len(data) > maximum_size or digest(data) != expected:
        raise ValueError(f"Input SHA-256 mismatch: {path.name}")
    return data


def write_new(root, relative, data):
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as output:
        output.write(data)


def write_json(root, relative, data):
    write_new(root, relative, (json.dumps(data, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


def png(red, green, blue):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    # Solid RGB panels have small sizes and visibly different pixel values.
    row = b"\0" + bytes((red, green, blue)) * 16
    header = struct.pack(">IIBBBBB", 16, 16, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(row * 16)) + chunk(b"IEND", b"")


def scene(fps, frames, *items, scene_id=SCENE_ID):
    # This describes a synthetic time domain. YMM4 itself must supply host evidence.
    return {"Id": scene_id, "VideoInfo": {"FPS": fps}, "R34ExpectedFrameCount": frames, "Items": list(items)}


def item(path, frame=0, *, media_type="ImageItem"):
    return {
        "$type": f"YukkuriMovieMaker.Project.Items.{media_type}, YukkuriMovieMaker",
        "Frame": frame,
        "Length": 1,
        "FilePath": path,
    }


def file_inventory(root, directory=None, expected=None):
    directory = root if directory is None else directory
    for path in sorted(directory.iterdir()):
        info = inspect_path(path)
        if stat.S_ISDIR(info.st_mode):
            yield from file_inventory(root, path, expected)
        elif stat.S_ISREG(info.st_mode):
            relative = path.relative_to(root).as_posix()
            if expected is not None and (relative not in expected or expected[relative]["bytes"] != info.st_size):
                raise ValueError("Fixture files or sizes differ from the approved manifest.")
            with path.open("rb") as source:
                sha256 = hashlib.file_digest(source, "sha256").hexdigest()
            yield relative, {"bytes": info.st_size, "sha256": sha256}
        else:
            raise ValueError(f"Unexpected fixture entry: {path}")


def fixture(files, expected, generated_by="R34 synthetic generator", absent=()):
    return {
        "files": list(files),
        "must_be_absent": list(absent),
        "expected": expected,
        "provenance": generated_by,
        "cleanup": "fixture copies only, after retaining required evidence",
        "real_ymm4_read": "NOT_VERIFIED",
    }


def create_synthetic(root):
    fixtures = {}
    markers = [("red", png(220, 10, 10)), ("green", png(10, 220, 10)), ("blue", png(10, 10, 220))]
    media = [f"pf/media/{color}.png" for color, _ in markers]
    for path, (_, data) in zip(media, markers, strict=True):
        write_new(root, path, data)
    for label, fps, seconds, change in (("a", 24, 130, 0), ("b", 30, 140, 1)):
        frames = fps * seconds
        positions = (0, 60 * fps - 1, 60 * fps, 120 * fps - 1, 120 * fps, frames - 1)
        items = [item(media[(i + change) % len(media)].removeprefix("pf/"), frame) for i, frame in enumerate(positions)]
        path = f"pf/{label}.ymmp"
        write_json(root, path, {"R34Revision": label, "Timelines": [scene(fps, frames, *items)]})
        fixtures[f"PF-{label.upper()}"] = fixture([path, *media], f"{fps} fps, {frames} frames; logical Scene {SCENE_ID}")
    write_json(root, "pf/c.ymmp", {"Timelines": [
        scene(24, 240, item("media/red.png", 239)),
        scene(24, 480, item("media/blue.png", 479), scene_id="00000000-0000-4000-8000-000000000002"),
    ]})
    fixtures["PF-C"] = fixture(["pf/c.ymmp", *media], "Two independently identifiable Scenes.")
    write_json(root, "pf/d.ymmp", {"Timelines": [scene(24, 24, item("missing/clip.png"), item("media/blue.png", 23))]})
    candidates = {"exact/clip.png": markers[0][1], "wrong-bytes/clip.png": markers[1][1], "wrong-name/other.png": markers[0][1]}
    for name, data in candidates.items():
        write_new(root, "relink/" + name, data)
    expectation = "relink/expected-material.json"
    write_json(root, expectation, {"Project": "pf/d.ymmp", "MissingReference": "missing/clip.png",
                                   "OriginalName": "clip.png", "Sha256": digest(markers[0][1]).upper()})
    fixtures["PF-D"] = fixture(["pf/d.ymmp", media[2], expectation, *["relink/" + name for name in candidates]],
                               "Only exact/clip.png meets both name and SHA-256.", absent=[MISSING])
    write_json(root, "pf/e.ymmp", {"Timelines": [scene(24, 1, item("media/green.png"))]})
    frames = [f"thumbnails/{slot:03d}.png" for slot in range(64)]
    for slot, path in enumerate(frames):
        write_new(root, path, png(slot, 0, 255 - slot))
    fixtures["PF-E"] = fixture(["pf/e.ymmp", "pf/a.ymmp", *media, *frames],
                               "One-frame source and a long source; 64 distinct input PNGs for cache/preview.")
    write_json(root, "compare/fixed.json", {"Version": 1, "DefaultOffsetTicks": 20_000_000, "Sections": []})
    write_json(root, "compare/sections.json", {"Version": 1, "DefaultOffsetTicks": -20_000_000, "Sections": [
        {"StartTicks": 600_000_000, "OffsetTicks": 100_000_000},
        {"StartTicks": 1_200_000_000, "OffsetTicks": -50_000_000},
    ]})
    fixtures["COMPARE"] = fixture(["pf/a.ymmp", "pf/b.ymmp", *media, "compare/fixed.json", "compare/sections.json"],
                                  "Fixed +2s; fallback -2s, [60s,120s) +10s, [120s,end) -5s.")
    legacy_list = "legacy/projects.json"
    legacy_rows = []
    for label in "abc":
        project_path = str(root / f"pf/{label}.ymmp")
        row = {"FullPath": project_path, "DisplayName": f"Synthetic project {label}",
               "LinkedYmmpFiles": [{"FilePath": project_path, "FutureLink": 7}]}
        if label != "c":
            row["ProjectId"] = "00000000-0000-4000-8000-000000000100"
        legacy_rows.append(row)
    write_json(root, legacy_list, {"Projects": legacy_rows, "Folders": [], "FutureRoot": {"Keep": True}})
    fixtures["LEGACY-LIST"] = fixture([legacy_list, "pf/a.ymmp", "pf/b.ymmp", "pf/c.ymmp", *media],
                                      "Report duplicate and legacy missing IDs; preserve unknown fields and source.")
    generation = "20261125-023000_012345ab"
    storage = "legacy/Generations/projects/" + "A" * 64
    historical = f"{storage}/generations/{generation}/project.ymmp"
    metadata = f"{storage}/generations/{generation}/metadata.json"
    manifest = f"{storage}/manifest.json"
    write_json(root, "legacy/current.ymmp", {"frame": 789})
    write_json(root, historical, {"frame": 123})
    write_new(root, manifest, b"{interrupted")
    previous = (root / historical).read_bytes()
    write_json(root, metadata, {"GenerationId": generation, "SourceProjectPath": str(root / "legacy/current.ymmp"),
                               "FileSize": len(previous), "Sha256": digest(previous).upper()})
    fixtures["LEGACY"] = fixture(["legacy/current.ymmp", historical, metadata, manifest],
                                 "Export the previous bytes to a new PF; keep current PF and corrupt manifest intact.",
                                 absent=["legacy/export.ymmp"])
    return fixtures


def add_ymmpx_inputs(root, fixtures):
    picture = "ymmpx/media/still.png"
    sequence = [f"ymmpx/media/shot{frame:02d}.png" for frame in (1, 2)]
    psd = "ymmpx/media/layers.psd"
    write_new(root, picture, png(255, 200, 50))
    for frame, path in enumerate(sequence, 1):
        write_new(root, path, png(100 * frame, 40, 80))
    # An uncompressed 1x1, 8-bit RGB PSD with no optional sections or extra layers.
    write_new(root, psd, struct.pack(">4sH6xHIIHHIIIH", b"8BPS", 1, 3, 1, 1, 8, 3, 0, 0, 0, 0) + b"\xff\0\0")
    project = "ymmpx/standard.ymmp"
    write_json(root, project, {"LayoutXml": "", "ToolStates": None, "FutureField": {"Keep": True},
                               "Items": [item("media/still.png"), item("media/shot01.png", media_type="VideoItem"),
                                         {**item("media/layers.psd"), "EnableLayers": [],
                                          "EnableLayersFilePath": "media/layers.psd"},
                                         item("media/absent.png")]})
    fixtures["YMMPX-PF"] = fixture([project, picture, *sequence, psd],
                                  "Normal resource, PNG sequence, PSD reference, absent file, optional UI state.",
                                  absent=[MISSING_YMMPX])
    valid = "exclude/import.json"
    invalid = "exclude/invalid.json"
    write_json(root, valid, [{"Path": "media/still.png", "IsFolder": False, "IsExcluded": True}])
    write_new(root, invalid, b'[{"Path":"media/still.png","IsExcluded":true,"IsExcluded":false}]\n')
    fixtures["EXCLUDE"] = fixture([valid, invalid, *fixtures["YMMPX-PF"]["files"]],
                                  "Import the valid filter; reject duplicate property before publishing.",
                                  absent=["ymmpx/standard.ymmpm-exclude.json"])


def create_standard_package(root, library_source):
    library_bytes = checked_bytes(library_source, YMMPX_LIB_HASH, 256 * 1024)
    with tempfile.TemporaryDirectory(prefix="p34pack-", dir=root.parent) as scratch:
        build = Path(scratch)
        write_new(build, "YmmpxLib.dll", library_bytes)
        write_new(build, "NuGet.Config", b"<configuration><packageSources><clear /></packageSources></configuration>")
        source = Path(__file__).with_name("r34_pack_fixture.cs").read_bytes()
        write_new(build, "Program.cs", source)
        write_new(build, "Pack.csproj", b"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
</Project>
""")
        project = root / "ymmpx/standard.ymmp"
        output = root / "ymmpx/standard.ymmpx"
        env = dict(os.environ, DOTNET_CLI_HOME=scratch, DOTNET_CLI_TELEMETRY_OPTOUT="1",
                   DOTNET_GENERATE_ASPNET_CERTIFICATE="false", DOTNET_NOLOGO="1",
                   TMP=scratch, TEMP=scratch)
        subprocess.run(["dotnet", "run", "--project", "Pack.csproj", "--no-launch-profile",
                        "--disable-build-servers", "-p:ImportDirectoryBuildProps=false",
                        "-p:ImportDirectoryBuildTargets=false", "-p:ImportDirectoryPackagesProps=false",
                        "--", str(project), str(output)],
                       cwd=build, env=env, check=True, timeout=120, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    with zipfile.ZipFile(output) as archive:
        expected = {
            "_ymmpx_project_path.txt": b"standard.ymmp",
            "resources/still.png": (root / "ymmpx/media/still.png").read_bytes(),
            "resources/sequence_1/shot01.png": (root / "ymmpx/media/shot01.png").read_bytes(),
            "resources/sequence_1/shot02.png": (root / "ymmpx/media/shot02.png").read_bytes(),
            "resources/layers.psd": (root / "ymmpx/media/layers.psd").read_bytes(),
        }
        if sorted(archive.namelist()) != sorted([*expected, "standard.ymmp", "links.json"]):
            raise ValueError("YmmpxLib did not produce the expected package members.")
        for name, content in expected.items():
            if archive.read(name) != content:
                raise ValueError(f"Unexpected standard package bytes: {name}")
        restored_shape = json.loads(archive.read("standard.ymmp"))
        if (not restored_shape["FutureField"]["Keep"] or restored_shape["Items"][3]["FilePath"] != "media/absent.png"
                or restored_shape["Items"][2]["EnableLayersFilePath"] != "resources/layers.psd"):
            raise ValueError("Unrelated fields or missing and PSD layer references changed unexpectedly.")


def generate(library=None, legacy=None):
    root = Path(tempfile.mkdtemp(prefix="p34f-", dir=local_temp_root()))
    try:
        inspect_ancestors(root)
        fixtures = create_synthetic(root)
        add_ymmpx_inputs(root, fixtures)
        unavailable = []
        if library is None:
            unavailable.append("YMMPX-1.1")
        else:
            create_standard_package(root, library)
            fixtures["YMMPX-1.1"] = fixture(["ymmpx/standard.ymmpx"], "Read standard 1.1 package.",
                                           f"Official YmmpxLib 1.1.0 {YMMPX_LIB_HASH}")
        for version, expected in LEGACY_HASHES.items():
            label = f"YMMPX-{version}"
            if legacy is None:
                unavailable.append(label)
                continue
            relative = f"{version}/sample.ymmpx"
            data = checked_bytes(legacy / relative, expected, 16 * 1024)
            write_new(root, f"ymmpx/legacy/{relative}", data)
            fixtures[label] = fixture([f"ymmpx/legacy/{relative}"], "Open with legacy classification; preserve original archive.",
                                      f"R23 synthetic sample created by released upstream CLI {version}")
        write_json(root, MANIFEST, {
            "schema": "ymmpm-r34-fixtures/1",
            "fixtures": fixtures,
            "unavailable": unavailable,
            "files": dict(file_inventory(root)),
        })
        manifest_sha = digest((root / MANIFEST).read_bytes())
        verify(root, manifest_sha)
        return root, manifest_sha
    except BaseException:
        # The directory is owned by this run. Preserve any partial evidence for diagnosis.
        print(f"Incomplete fixture directory: {root}")
        raise


def verify(root, manifest_sha):
    root = root.absolute()
    approved_sha = manifest_sha.lower()
    payload = checked_bytes(root / MANIFEST, approved_sha, 1024 * 1024)
    manifest = json.loads(payload)
    if manifest["schema"] != "ymmpm-r34-fixtures/1":
        raise ValueError("Unexpected fixture manifest schema.")
    for row in manifest["fixtures"].values():
        for path in row["must_be_absent"]:
            if os.path.lexists(root / path):
                raise ValueError("An expected missing input or output was found.")
    approved = {**manifest["files"], MANIFEST: {"bytes": len(payload), "sha256": approved_sha}}
    if dict(file_inventory(root, expected=approved)) != approved:
        raise ValueError("Fixture files or SHA-256 values differ from the approved manifest.")
    return len(manifest["files"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    create = commands.add_parser("create")
    create.add_argument("--ymmpx-lib", type=Path, help="Isolated official YmmpxLib 1.1.0 DLL.")
    create.add_argument("--legacy-root", type=Path, help="Parent of the three pinned R23 legacy sample folders.")
    check = commands.add_parser("verify")
    check.add_argument("root", type=Path)
    check.add_argument("manifest_sha256")
    args = parser.parse_args()
    try:
        if args.command == "create":
            root, manifest_sha = generate(args.ymmpx_lib, args.legacy_root)
            count = verify(root, manifest_sha)
            print(json.dumps({"root": str(root), "manifest_sha256": manifest_sha, "files_verified": count}))
        else:
            print(json.dumps({"files_verified": verify(args.root, args.manifest_sha256), "status": "verified"}))
        return 0
    except subprocess.CalledProcessError as error:
        parser.exit(1, f"YmmpxLib fixture build failed (exit {error.returncode}): {error.output.decode(errors='replace')}\n")
    except (OSError, ValueError, KeyError, subprocess.TimeoutExpired, zipfile.BadZipFile) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
