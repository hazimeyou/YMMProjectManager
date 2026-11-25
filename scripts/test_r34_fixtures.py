import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from r34_fixtures import MANIFEST, MISSING, checked_bytes, generate, local_temp_root, verify, write_new


class FixtureTests(unittest.TestCase):
    def setUp(self):
        parent = self.enterContext(tempfile.TemporaryDirectory(prefix="p34test-", dir=local_temp_root()))
        self.enterContext(patch("r34_fixtures.local_temp_root", return_value=Path(parent)))
        self.root, self.sha256 = generate()

    def read_json(self, path):
        return json.loads((self.root / path).read_bytes())

    def test_inventory_and_no_library_status(self):
        manifest = self.read_json(MANIFEST)
        self.assertEqual(verify(self.root, self.sha256), len(manifest["files"]))
        self.assertEqual(manifest["unavailable"], ["YMMPX-1.1", "YMMPX-v0.2.1", "YMMPX-v0.3.0", "YMMPX-v1.0.1"])
        self.assertTrue(all(row["real_ymm4_read"] == "NOT_VERIFIED" for row in manifest["fixtures"].values()))
        self.assertEqual(len(list((self.root / "thumbnails").glob("*.png"))), 64)

    def test_same_logical_scene_with_different_rate_and_revision(self):
        original = self.read_json("pf/a.ymmp")
        revised = self.read_json("pf/b.ymmp")
        left, right = original["Timelines"][0], revised["Timelines"][0]
        self.assertNotEqual(original["R34Revision"], revised["R34Revision"])
        self.assertEqual(left["Id"], right["Id"])
        self.assertEqual((left["VideoInfo"]["FPS"], left["R34ExpectedFrameCount"]), (24, 3120))
        self.assertEqual((right["VideoInfo"]["FPS"], right["R34ExpectedFrameCount"]), (30, 4200))
        self.assertNotEqual(self.read_json(MANIFEST)["files"]["pf/a.ymmp"]["sha256"],
                            self.read_json(MANIFEST)["files"]["pf/b.ymmp"]["sha256"])
        multi = self.read_json("pf/c.ymmp")["Timelines"]
        self.assertEqual(len({scene["Id"] for scene in multi}), 2)

    def test_relink_candidate_bytes_and_names(self):
        exact = self.root / "relink/exact/clip.png"
        wrong_bytes = self.root / "relink/wrong-bytes/clip.png"
        wrong_name = self.root / "relink/wrong-name/other.png"
        self.assertFalse((self.root / MISSING).exists())
        self.assertEqual(exact.name, wrong_bytes.name)
        self.assertNotEqual(exact.read_bytes(), wrong_bytes.read_bytes())
        self.assertNotEqual(exact.name, wrong_name.name)
        self.assertEqual(exact.read_bytes(), wrong_name.read_bytes())
        manifest = self.read_json(MANIFEST)
        expected = self.read_json("relink/expected-material.json")
        self.assertEqual(expected["OriginalName"], exact.name)
        self.assertEqual(expected["Sha256"].lower(), manifest["files"]["relink/exact/clip.png"]["sha256"])
        self.assertEqual(expected["MissingReference"], self.read_json(expected["Project"])["Timelines"][0]["Items"][0]["FilePath"])

    def test_same_length_tampering_fails_sha256(self):
        path = self.root / "pf/a.ymmp"
        path.write_bytes(path.read_bytes().replace(b'"a"', b'"z"', 1))
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            verify(self.root, self.sha256)

    def test_manifest_tampering_fails_detached_digest(self):
        (self.root / MANIFEST).write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Input SHA-256 mismatch"):
            verify(self.root, self.sha256)

    def test_detects_expected_missing_paths(self):
        (self.root / MISSING).mkdir(parents=True)
        with self.assertRaisesRegex(ValueError, "expected missing"):
            verify(self.root, self.sha256)

    def test_detects_unlisted_files(self):
        write_new(self.root, "unexpected.txt", b"other fixture")
        with self.assertRaisesRegex(ValueError, "differ from the approved manifest"):
            verify(self.root, self.sha256)

    def test_existing_file_stays_intact(self):
        path = self.root / "pf/a.ymmp"
        before = path.read_bytes()
        with self.assertRaises(FileExistsError):
            write_new(self.root, "pf/a.ymmp", b"replacement")
        self.assertEqual(path.read_bytes(), before)
        verify(self.root, self.sha256)
        second_root, second_sha256 = generate()
        self.assertNotEqual(self.root, second_root)
        verify(second_root, second_sha256)
        verify(self.root, self.sha256)

    def test_expected_provenance_hash_must_match(self):
        path = self.root / "pf/a.ymmp"
        with self.assertRaisesRegex(ValueError, "Input SHA-256 mismatch"):
            checked_bytes(path, "f" * 64, 4096)

    def test_legacy_metadata_represents_the_intended_inputs(self):
        data = self.read_json("legacy/projects.json")
        rows = data["Projects"]
        self.assertEqual(rows[0]["ProjectId"], rows[1]["ProjectId"])
        self.assertNotIn("ProjectId", rows[2])
        self.assertTrue(data["FutureRoot"]["Keep"])
        self.assertTrue(all(file["FutureLink"] == 7 for row in rows for file in row["LinkedYmmpFiles"]))
        manifest = self.read_json(MANIFEST)
        paths = manifest["fixtures"]["LEGACY"]["files"]
        metadata = self.read_json(next(path for path in paths if path.endswith("metadata.json")))
        snapshot = next(path for path in paths if path.endswith("project.ymmp"))
        self.assertEqual(metadata["SourceProjectPath"], str(self.root / "legacy/current.ymmp"))
        self.assertEqual(metadata["Sha256"].lower(), manifest["files"][snapshot]["sha256"])

    @unittest.skipUnless(os.name == "nt", "Windows-only temp-path contract")
    def test_windows_temp_root_ignores_unsafe_ambient_temp(self):
        with patch.object(tempfile, "gettempdir", side_effect=AssertionError("ambient write probe")):
            self.assertEqual(local_temp_root(), (Path(os.environ["LOCALAPPDATA"]) / "Temp").resolve())
            for unsafe in (r"\\invalid.invalid\share", r"\\?\C:\example", r"C:\example:stream"):
                with patch.dict(os.environ, LOCALAPPDATA=unsafe):
                    with self.assertRaises(ValueError):
                        local_temp_root()


if __name__ == "__main__":
    unittest.main()
