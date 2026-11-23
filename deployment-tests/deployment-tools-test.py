"""Exercise the production archive stager and shell checker using temporary local files."""
import hashlib
import io
import os
from pathlib import Path
import stat
import subprocess
import sys
import tarfile
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
BASH = str(Path(os.environ["ProgramFiles"]) / "Git/bin/bash.exe") if os.name == "nt" else "bash"


class DeploymentToolsTests(unittest.TestCase):
    def test_update_requires_a_protected_server_or_an_offline_arena(self):
        with tempfile.TemporaryDirectory() as tmp:
            result = self.bash(Path(tmp), "-ec", '''
                source "$DEPLOY_SCRIPT"
                python3() { "$TEST_PYTHON" "$@"; }
                systemctl() { echo "$fake_service_state"; }
                curl() { echo "$health"; }
                fake_service_state=active
                health='{"status":"ok"}'
                if check_running_arena_guard; then exit 1; fi
                health='{"status":"ok","matchUpdateLock":true}'
                check_running_arena_guard
                fake_service_state=activating
                if check_running_arena_guard; then exit 1; fi
                fake_service_state=inactive
                health=''
                check_running_arena_guard
                fake_service_state=failed
                check_running_arena_guard
            ''')
            self.assertEqual(0, result.returncode, result.stderr)

    def bash(self, work, *args):
        env = dict(os.environ, TEST_PYTHON=sys.executable, DEPLOY_SCRIPT=(ROOT / "deployment/deploy.sh").as_posix())
        if os.name == "nt":
            env["MSYS"] = "winsymlinks:sys"
        return subprocess.run([BASH, *args], cwd=work, env=env, text=True,
                              capture_output=True, timeout=15)

    def stage(self, work, extra=None, digest=None):
        source = work / "upload.tar.gz"
        with tarfile.open(source, "w:gz") as tar:
            folder = tarfile.TarInfo("./")
            folder.type = tarfile.DIRTYPE
            tar.addfile(folder)
            data = b"game results"
            member = tarfile.TarInfo("./docs/score.txt")
            member.size, member.mode = len(data), 0o777
            tar.addfile(member, io.BytesIO(data))
            if extra is not None:
                tar.addfile(extra)
        if digest is None:
            digest = hashlib.sha256(source.read_bytes()).hexdigest()
        return self.bash(work, "-ec", '''
            source "$DEPLOY_SCRIPT"
            python3() { "$TEST_PYTHON" "$@"; }
            RELEASE_ROOT="$PWD/releases"
            CURRENT_LINK="$PWD/current"
            NEXT_LINK="$PWD/.current-next"
            mkdir -p releases/known
            (umask 077; mkdir staging)
            activate_release "$RELEASE_ROOT/known"
            stage_release_archive upload.tar.gz staging "$1"
            mv staging/publish releases/candidate
            activate_release "$RELEASE_ROOT/candidate"
        ''', "stage-test", digest)

    def assert_active(self, work, name):
        link = self.bash(work, "-c", "readlink current")
        self.assertEqual(link.returncode, 0, link.stderr)
        self.assertEqual("releases/" + name, link.stdout.strip())

    def test_regular_bundle_uses_private_copy_and_ordinary_file_permissions(self):
        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp)
            result = self.stage(work)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assert_active(work, "candidate")
            score = work / "releases/candidate/docs/score.txt"
            self.assertEqual(b"game results", score.read_bytes())
            private = work / "staging/release.tar.gz"
            self.assertEqual((work / "upload.tar.gz").read_bytes(), private.read_bytes())
            if os.name == "posix":
                self.assertEqual(0o600, stat.S_IMODE(private.stat().st_mode))
                self.assertEqual(0o600, stat.S_IMODE(score.stat().st_mode))
                if os.geteuid() == 0:
                    import pwd
                    self.assertEqual(0, private.stat().st_uid)
                    self.assertEqual(pwd.getpwnam("nobody").pw_uid, score.stat().st_uid)

    def test_rejects_non_regular_or_conflicting_members_before_writing_any_files(self):
        cases = [(tarfile.SYMTYPE, "score-link"), (tarfile.LNKTYPE, "score-copy"),
                 (tarfile.FIFOTYPE, "queue"), (tarfile.REGTYPE, "docs/./score.txt"),
                 (tarfile.REGTYPE, "docs"), (tarfile.REGTYPE, "bad\\name")]
        for kind, name in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as tmp:
                work, member = Path(tmp), tarfile.TarInfo(name)
                member.type = kind
                if kind in (tarfile.SYMTYPE, tarfile.LNKTYPE):
                    member.linkname = "docs/score.txt"
                result = self.stage(work, member)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Invalid release archive", result.stderr)
                self.assertEqual([], list((work / "staging/publish").iterdir()))
                self.assert_active(work, "known")

    def test_digest_mismatch_preserves_selected_release(self):
        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp)
            result = self.stage(work, digest="0" * 64)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("checksum", result.stderr)
            self.assertFalse((work / "staging/publish").exists())
            self.assert_active(work, "known")

    def test_shell_check_detects_an_invalid_second_script(self):
        check = (ROOT / "deployment-tests/check-shell-syntax.sh").as_posix()
        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp)
            (work / "first.sh").write_text("true\n")
            (work / "second.sh").write_text("if true; then\n")
            result = self.bash(work, check, "first.sh", "second.sh")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("second.sh", result.stderr)
            (work / "second.sh").write_text("true\n")
            result = self.bash(work, check, "first.sh", "second.sh")
            self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
