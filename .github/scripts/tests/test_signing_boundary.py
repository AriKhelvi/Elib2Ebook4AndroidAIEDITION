"""Регрессии границы доступа к ключу: проект собирается без секрета владельца."""

from pathlib import Path
import re
import unittest


WORKFLOW = Path(__file__).resolve().parents[2] / "workflows" / "android.yml"


class SigningBoundaryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = WORKFLOW.read_text(encoding="utf-8")
        # Только верхний уровень jobs; вложенные run/env не разбираются как задания.
        cls.jobs = dict(re.findall(r"^  ([a-z_]+):\n(.*?)(?=^  [a-z_]+:\n|\Z)",
                                   source.split("\njobs:\n", 1)[1], re.M | re.S))

    def test_compiler_has_no_repository_secrets_or_write_token(self):
        build = self.jobs["apk"]
        self.assertNotIn("secrets.", build)
        self.assertNotIn("contents: write", build)
        self.assertIn("contents: read", build)
        self.assertIn("persist-credentials: false", build)
        self.assertIn("dotnet publish", build)
        self.assertNotIn("android_release.py attach", build)

    def test_owner_signing_cannot_run_for_pull_request(self):
        signing = self.jobs["sign"]
        condition = re.search(r"^    if: (.+)$", signing, re.M).group(1)
        self.assertIn("github.event_name != 'pull_request'", condition)
        self.assertIn("needs.prepare.outputs.build == 'true'", condition)
        self.assertIn("needs: [prepare, apk]", signing)
        self.assertIn("runs-on: ubuntu-latest", signing)

    def test_signing_job_does_not_execute_project_or_reuse_its_workspace(self):
        signing = self.jobs["sign"]
        self.assertNotRegex(signing, r"(?m)^\s+(?:run: )?(?:dotnet|msbuild|npm|gradle)\s")
        self.assertNotIn("actions/cache", signing)
        self.assertIn("artifact-ids: ${{ needs.apk.outputs.artifact }}", signing)
        self.assertIn("apksigner\" sign", signing)
        self.assertIn("apksigner\" verify", signing)

    def test_owner_password_is_scoped_to_signing_step_and_never_argv(self):
        signing = self.jobs["sign"]
        self.assertEqual(signing.count("secrets.E2E_KEYSTORE_PASSWORD"), 1)
        self.assertIn("--ks-pass env:E2E_KS_PASS", signing)
        self.assertIn("--key-pass env:E2E_KS_PASS", signing)
        self.assertNotIn('>> "$GITHUB_ENV"', signing)
        self.assertIn("trap 'rm -f --", signing)
        self.assertIn("unset KEYSTORE_BASE64 E2E_KS_PASS", signing)


if __name__ == "__main__":
    unittest.main()
